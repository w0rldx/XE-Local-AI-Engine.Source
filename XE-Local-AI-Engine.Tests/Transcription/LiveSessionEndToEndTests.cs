namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The slice's gate: one live session driven the way a browser drives it, over the WIRED host.
///     <para>
///         Only <see cref="IWhisperTranscriber" /> is substituted, because a local whisper daemon is the one thing a
///         test cannot have. The registry, the segmenter, the hub, the store and the column encryption are all real —
///         which is the point: every other live test in this slice hands the registry a
///         <see cref="LiveSessionOptions" /> itself, and that is precisely how a missing start path stays invisible.
///     </para>
/// </summary>
[NotInParallel(LiveSessionKey)]
public sealed class LiveSessionEndToEndTests
{
    /// <summary>
    ///     Shared with nothing else on purpose: these tests build a whole host each and drive real inference lanes, so
    ///     they are serialized against each other rather than against the module.
    /// </summary>
    private const string LiveSessionKey = nameof(LiveSessionEndToEndTests);

    private const string ApiPrefix = "/api/local/v1";

    /// <summary>One second of PCM per frame — 32 000 bytes, comfortably under the hub's 32 KiB ceiling.</summary>
    private const long FrameMs = 1_000;

    /// <summary>The session's capture window; the lane force-commits when the uncommitted span reaches it.</summary>
    private const long WindowMs = 5_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Create → subscribe → start → push → commit, with <b>no</b> registry preseed: at no point does this test
    ///     touch <see cref="ILiveTranscriptionSessionRegistry" />.
    /// </summary>
    /// <remarks>
    ///     Deleting the <c>live/start</c> call must make this fail with the hub's <c>NotTranscribing</c> error, and
    ///     that is what the test is for: a session nobody started has no lanes, so the hub has nowhere to put audio.
    /// </remarks>
    [Test]
    [NotInParallel(LiveSessionKey)]
    public async Task CreateSubscribeStartPushCommit_WithNoRegistryPreseed_DeliversACommittedSegment()
    {
        var transcriber = OneSegmentPerWindow();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(client, factory).ConfigureAwait(false);

        var committed = new TaskCompletionSource<TranscriptSegmentCommittedPush>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Connect(factory);
        _ = connection.On<TranscriptSegmentCommittedPush>(TranscriptionHubEvents.SegmentCommitted, push =>
        {
            if (push.SessionId == sessionId)
            {
                _ = committed.TrySetResult(push);
            }
        });
        await connection.StartAsync().ConfigureAwait(false);

        var snapshot = await connection.InvokeAsync<TranscriptionSessionSubscriptionSnapshot>("SubscribeSession", sessionId, 0L).ConfigureAwait(false);
        AssertEx.Empty(snapshot.Segments, "a session that has heard nothing has no transcript to replay.");
        AssertEx.Equal(expected: 0L, snapshot.LastSeq);
        AssertEx.False(snapshot.ReplayTruncated);

        using (var started = await SendAsync(client, factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/live/start").ConfigureAwait(false))
        {
            AssertEx.Equal(HttpStatusCode.OK, started.StatusCode);
        }

        await PushWindowAsync(connection, sessionId, fromMs: 0).ConfigureAwait(false);

        var push = await committed.Task.WaitAsync(TestBudgets.Contended).ConfigureAwait(false);
        AssertEx.Equal(expected: 1L, push.Seq, "the first live commit of a session is sequence one, never zero.");
        AssertEx.Equal("Mono", push.Channel, "a microphone session carries one undifferentiated lane.");
        AssertEx.Equal("hello from the lane", push.Text);

        // The push is not the record: what reaches the operator's transcript is the row, read back over REST through
        // the real encrypted column.
        var session = await ReadSessionAsync(client, factory, sessionId).ConfigureAwait(false);
        var segments = session.GetProperty("segments");
        AssertEx.Equal(expected: 1, segments.GetArrayLength());
        AssertEx.Equal(expected: 1L, segments[0].GetProperty("seq").GetInt64());
        AssertEx.Equal("hello from the lane", segments[0].GetProperty("text").GetString());
        AssertEx.Equal(push.Channel, segments[0].GetProperty("channel").GetString(), "the replayed row and the live push spell the channel the same way.");
    }

    /// <summary>
    ///     A double-click, a retried fetch or a reconnect that re-issues the start must not register a second set of
    ///     lanes: two lanes on one session would transcribe the same audio twice and allocate two sequences for it.
    /// </summary>
    [Test]
    [NotInParallel(LiveSessionKey)]
    public async Task Start_CalledTwice_IsIdempotentAndDoesNotDuplicateLanes()
    {
        var transcriber = OneSegmentPerWindow();
        await using var factory = FactoryWith(transcriber);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(client, factory).ConfigureAwait(false);

        var pushes = new ConcurrentQueue<TranscriptSegmentCommittedPush>();
        await using var connection = Connect(factory);
        _ = connection.On<TranscriptSegmentCommittedPush>(TranscriptionHubEvents.SegmentCommitted, push =>
        {
            if (push.SessionId == sessionId)
            {
                pushes.Enqueue(push);
            }
        });
        await connection.StartAsync().ConfigureAwait(false);
        _ = await connection.InvokeAsync<TranscriptionSessionSubscriptionSnapshot>("SubscribeSession", sessionId, 0L).ConfigureAwait(false);

        string firstBody;
        string secondBody;
        using (var first = await SendAsync(client, factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/live/start").ConfigureAwait(false))
        {
            AssertEx.Equal(HttpStatusCode.OK, first.StatusCode);
            firstBody = await first.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        using (var second = await SendAsync(client, factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/live/start").ConfigureAwait(false))
        {
            AssertEx.Equal(HttpStatusCode.OK, second.StatusCode, "the route is idempotent: starting a live session twice is not an error.");
            secondBody = await second.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        AssertEx.Equal(firstBody, secondBody, "the second start reports the state the first one left, verbatim.");

        await PushWindowAsync(connection, sessionId, fromMs: 0).ConfigureAwait(false);
        await AssertEx.EventuallyAsync(() => !pushes.IsEmpty, TestBudgets.Contended, "the session must still transcribe after the repeated start.").ConfigureAwait(false);

        var session = await ReadSessionAsync(client, factory, sessionId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1,
            session.GetProperty("segments").GetArrayLength(),
            "one window of audio produced one row: a second set of lanes would have transcribed it again.");
        AssertEx.Equal(expected: 1, pushes.Count, "and announced it once.");
    }

    /// <summary>
    ///     A row reading <c>Transcribing</c> with no registry entry behind it accepts no audio and never ends, which
    ///     is strictly worse than a start the caller can see failed.
    /// </summary>
    [Test]
    [NotInParallel(LiveSessionKey)]
    public async Task Start_WhenRegistrationFails_LeavesTheRowInCreated()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        registry.StartLiveSessionAsync(Arg.Any<Guid>(), Arg.Any<LiveSessionOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => throw new InvalidOperationException("registration refused"));
        await using var factory = FactoryWith(OneSegmentPerWindow(), services =>
        {
            services.RemoveAll<ILiveTranscriptionSessionRegistry>();
            services.AddSingleton(registry);
        });
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(client, factory).ConfigureAwait(false);

        using (var started = await SendAsync(client, factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/live/start").ConfigureAwait(false))
        {
            AssertEx.Equal(HttpStatusCode.InternalServerError, started.StatusCode, "the failure is surfaced, not swallowed.");
        }

        var session = await ReadSessionAsync(client, factory, sessionId).ConfigureAwait(false);
        AssertEx.Equal("Created",
            session.GetProperty("session").GetProperty("status").GetString(),
            "the row rolled back rather than being stranded in Transcribing with nothing behind it.");
    }

    /// <summary>
    ///     A transcriber that answers only once the lane submits a whole window, so exactly one segment commits per
    ///     <see cref="WindowMs" /> of audio. The tick submissions below the cap return nothing, as a silent stretch does.
    /// </summary>
    private static ScriptedWhisperTranscriber OneSegmentPerWindow() =>
        new(window => window.DurationMs >= WindowMs
            ?
            [
                new WhisperTranscriptSegment(0.5, 4.0, "hello from the lane", 0.87)
            ]
            : []);

    /// <summary>Pushes one whole capture window as one-second frames, the way a browser worklet does.</summary>
    private static async Task PushWindowAsync(HubConnection connection, Guid sessionId, long fromMs)
    {
        for (var offset = 0L; offset < WindowMs; offset += FrameMs)
        {
            var frame = LivePcm.Range(fromMs + offset, fromMs + offset + FrameMs).ToArray();
            AssertEx.True(frame.Length <= TranscriptionHub.MaxFrameBytes, $"a {frame.Length}-byte frame is past the hub's ceiling.");
            await connection.InvokeAsync("PushAudioFrame", sessionId, (int)TranscriptChannel.Mono, frame).ConfigureAwait(false);
        }
    }

    private static HubConnection Connect(TestServerWebAppFactory factory) =>
        new HubConnectionBuilder()
            .WithUrl("http://localhost" + LocalApiRoutes.Transcription.Hub,
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(factory.CreateNodeAccessToken());
                    options.Headers.Add("Origin", "http://localhost");
                })
            .Build();

    private static async Task<Guid> CreateSessionAsync(HttpClient client, TestServerWebAppFactory factory)
    {
        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions");
        request.Content = JsonContent.Create(new
        {
            title = "live round",
            sourceKind = nameof(TranscriptionSourceKind.Microphone),
            modelId = "tiny",
            maxWindowSeconds = (int)(WindowMs / 1_000)
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        var body = await ReadJsonAsync(response).ConfigureAwait(false);
        return body.GetProperty("session").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ReadSessionAsync(HttpClient client, TestServerWebAppFactory factory, Guid sessionId)
    {
        using var response = await SendAsync(client, factory, HttpMethod.Get, $"{ApiPrefix}/transcription/sessions/{sessionId}").ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        using var request = Authorized(factory, method, route);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions);
    }

    private static TestServerWebAppFactory FactoryWith(IWhisperTranscriber transcriber, Action<IServiceCollection>? extra = null) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                // The ONLY substitution in the default case: a local whisper daemon is the one thing this host cannot
                // have. Everything the slice added stays real.
                services.RemoveAll<IWhisperTranscriber>();
                services.AddSingleton(transcriber);
                extra?.Invoke(services);
            }
        };
}
