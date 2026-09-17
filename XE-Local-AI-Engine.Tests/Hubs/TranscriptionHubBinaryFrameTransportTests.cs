namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one wire fact the browser capture path is built on: a PCM frame reaches
///     <see cref="ILiveTranscriptionSessionRegistry.PushAudioAsync" /> byte-for-byte when the client passes it as a
///     base64 <see cref="string" />, exactly as a <c>byte[]</c> from a .NET caller does.
///     <para>
///         This node runs no MessagePack, so the hub speaks the JSON protocol and its server side is
///         System.Text.Json, which reads a JSON string into a <c>byte[]</c> as base64. The JavaScript client's JSON
///         protocol is <c>JSON.stringify</c>, and that turns a <c>Uint8Array</c> into <c>{"0":12,"1":…}</c> rather
///         than a base64 string — so the browser has to base64-encode each frame itself and invoke
///         <see cref="XE_Local_AI_Engine.Client.Hubs.TranscriptionHub.PushAudioFrame" /> with a <c>string</c>. That
///         argument form is what the second half of this test proves, over the real host and a real
///         <see cref="HubConnection" />; without it the browser path is a guess.
///     </para>
///     <para>
///         The return half of the same contract is
///         <see cref="SubscribeSession_OverTheJsonProtocol_ReturnsTheSnapshotInTheCamelCaseShapeTheClientSchemaParses" />.
///         The browser parses what <c>SubscribeSession</c> answers with a zod schema that spells every property in
///         camelCase, so that snapshot is read here as raw JSON and its property NAMES asserted: a rename on either
///         side fails <c>safeParse</c>, and a failed parse renders an empty transcript with no error anywhere.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptionHubBinaryFrameTransportTests
{
    private const string ApiPrefix = "/api/local/v1";

    /// <summary>The frame, as the base64 the browser client puts on the wire. Eight bytes: even, non-empty, tiny.</summary>
    private const string PayloadBase64 = "AAECAwQFBgc=";

    /// <summary>The same frame as bytes, which is what the registry must receive either way.</summary>
    private static readonly byte[] Payload = [0, 1, 2, 3, 4, 5, 6, 7];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task PushAudioFrame_OverTheJsonProtocol_BindsAByteArrayAndItsBase64StringIdentically()
    {
        var frames = new ConcurrentQueue<(TranscriptChannel Channel, byte[] Pcm)>();
        var registry = RecordingRegistry(frames);
        await using var factory = FactoryWith(registry);
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(client, factory);
        using (var started = await SendAsync(client, factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/live/start"))
        {
            AssertEx.Equal(HttpStatusCode.OK, started.StatusCode, await started.Content.ReadAsStringAsync());
        }

        await using var connection = Connect(factory);
        await connection.StartAsync();

        // The invoke itself is the gate: it completes only once the hub's own PushAudioFrame has returned, so the
        // registry call it forwards is already recorded and nothing here waits on a clock.
        await connection.InvokeAsync("PushAudioFrame", sessionId, (int)TranscriptChannel.Mono, Payload);

        var (bytesChannel, bytesPcm) = Dequeue(frames, "the byte[] invoke must reach the registry.");
        AssertEx.Equal(TranscriptChannel.Mono, bytesChannel);
        AssertEx.Equal(PayloadBase64, Convert.ToBase64String(bytesPcm), "a byte[] frame must arrive unchanged.");

        // The browser's form. A HubException here is the operator decision the slice plan calls out (MessagePack on
        // both sides), NOT something to work around by reshaping the hub signature.
        await connection.InvokeAsync("PushAudioFrame", sessionId, (int)TranscriptChannel.Mono, PayloadBase64);

        var (stringChannel, stringPcm) = Dequeue(frames, "the base64-string invoke must reach the registry: this is the contract the browser client depends on.");
        AssertEx.Equal(TranscriptChannel.Mono, stringChannel);
        AssertEx.Equal(PayloadBase64,
            Convert.ToBase64String(stringPcm),
            "System.Text.Json must decode the JSON string into the same eight bytes the byte[] form delivered.");

        AssertEx.Empty(frames, "two invokes are two frames; a third would mean something replayed one.");
    }

    /// <summary>
    ///     The shape the client's <c>transcriptionSnapshotSchema</c> demands, taken from the real host over the real
    ///     JSON protocol with a real committed row behind it.
    /// </summary>
    /// <remarks>
    ///     Read as <see cref="JsonElement" /> on purpose: deserializing into the server's own record would agree with
    ///     itself whatever the wire said. The rows are the S2 REST DTO, so the generated client covers their spelling
    ///     — but nothing covered the envelope, and a drifted name there is silent: <c>safeParse</c> fails, the
    ///     subscription never resolves and every live push is buffered behind a transcript that renders empty.
    /// </remarks>
    [Test]
    public async Task SubscribeSession_OverTheJsonProtocol_ReturnsTheSnapshotInTheCamelCaseShapeTheClientSchemaParses()
    {
        // No registry substitute: this test needs a row the real store wrote through the real encrypted column, and
        // the snapshot is replayed from the transcript rather than from anything the registry holds.
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var sessionId = await CreateSessionAsync(client, factory);

        // The same call the registry's commit pipeline makes; the row is therefore indistinguishable from a live one.
        await factory.Services.GetRequiredService<ITranscriptionService>()
                     .AppendLiveSegmentAsync(sessionId,
                         seq: 1,
                         TranscriptChannel.Mono,
                         startMs: 500,
                         endMs: 4_000,
                         "the browser parses this row",
                         confidence: 0.87,
                         CancellationToken.None);

        await using var connection = Connect(factory);
        await connection.StartAsync();

        var snapshot = await connection.InvokeAsync<JsonElement>("SubscribeSession", sessionId, 0L);

        AssertEx.Equal(sessionId, Property(snapshot, "sessionId").GetGuid());
        AssertEx.Equal("Created", Property(snapshot, "status").GetString(), "a session that was never started is still in its created state.");
        AssertEx.Equal(expected: 1L, Property(snapshot, "lastSeq").GetInt64(), "the watermark is the last row the subscriber was handed.");
        AssertEx.False(Property(snapshot, "replayTruncated").GetBoolean(), "one row is not a truncated page.");

        var segments = Property(snapshot, "segments");
        AssertEx.Equal(JsonValueKind.Array, segments.ValueKind, "the schema parses 'segments' with z.array.");
        AssertEx.Equal(expected: 1, segments.GetArrayLength());

        var row = segments[0];
        AssertEx.Equal(expected: 1L, Property(row, "seq").GetInt64());
        AssertEx.Equal(expected: 500L, Property(row, "startMs").GetInt64());
        AssertEx.Equal(expected: 4_000L, Property(row, "endMs").GetInt64());
        AssertEx.Equal("the browser parses this row", Property(row, "text").GetString());
        AssertEx.Equal("Mono",
            Property(row, "channel").GetString(),
            "the channel is one of Mono/You/Others, spelled exactly as the REST rows spell it: fromWireChannel matches on those three.");
        AssertEx.Equal(expected: 0.87, Property(row, "confidence").GetDouble(), "confidence is optional to the schema but must be a number when the row has one.");
    }

    /// <summary>
    ///     Reads one property and names every property that WAS there when it is missing, so a rename reports the
    ///     drift rather than a bare "property not found".
    /// </summary>
    private static JsonElement Property(JsonElement element, string name)
    {
        AssertEx.True(element.TryGetProperty(name, out var value),
            $"the client schema requires '{name}'; the payload carried [{string.Join(", ", element.EnumerateObject().Select(static property => property.Name))}].");
        return value;
    }

    private static (TranscriptChannel Channel, byte[] Pcm) Dequeue(ConcurrentQueue<(TranscriptChannel Channel, byte[] Pcm)> frames, string message)
    {
        AssertEx.True(frames.TryDequeue(out var frame), message);
        return frame;
    }

    /// <summary>
    ///     The registry is substituted rather than run for real because what is under test is the transport: the
    ///     bytes that come out the far side of the JSON protocol, not what a lane later does with them. It answers
    ///     <c>IsLive</c> so the hub forwards instead of refusing, and records every pushed frame as its own array.
    /// </summary>
    private static ILiveTranscriptionSessionRegistry RecordingRegistry(ConcurrentQueue<(TranscriptChannel Channel, byte[] Pcm)> frames)
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        registry.IsLive(Arg.Any<Guid>()).Returns(returnThis: true);
        _ = registry.PushAudioAsync(Arg.Any<Guid>(), Arg.Any<TranscriptChannel>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        // Copied here, not held: the memory belongs to the protocol's buffer once this returns.
                        frames.Enqueue(((TranscriptChannel)call[1], ((ReadOnlyMemory<byte>)call[2]).ToArray()));
                        return Task.CompletedTask;
                    });
        return registry;
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
            title = "frame transport",
            sourceKind = nameof(TranscriptionSourceKind.Microphone),
            modelId = "tiny",
            maxWindowSeconds = 5
        });
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

        var payload = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(payload, JsonOptions).GetProperty("session").GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        using var request = Authorized(factory, method, route);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static TestServerWebAppFactory FactoryWith(ILiveTranscriptionSessionRegistry registry) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ILiveTranscriptionSessionRegistry>();
                services.AddSingleton(registry);
            }
        };
}
