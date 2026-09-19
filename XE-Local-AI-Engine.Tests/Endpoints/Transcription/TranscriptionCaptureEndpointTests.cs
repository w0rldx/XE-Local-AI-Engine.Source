namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three per-application capture routes and the capability flag the SPA gates on: who may call them, and how
///     each refusal reaches the wire as something a client can branch on.
/// </summary>
/// <remarks>
///     The unsupported cases use the REAL <see cref="NotSupportedProcessAudioCaptureSource" /> rather than a stub,
///     because it is the implementation every non-Windows node actually resolves and it is small enough to be the
///     thing under test.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class TranscriptionCaptureEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task CaptureRoutes_WithAnAuthenticatedNonOperator_AreForbidden()
    {
        // THIS is the Operator proof. An anonymous 401 is not: swapping Policies(Operator) for a plain [Authorize]
        // keeps the anonymous case green while opening the route to any signed-in principal.
        var sessionId = Guid.NewGuid();
        (HttpMethod Method, string Route)[] routes =
        [
            (HttpMethod.Get, $"{ApiPrefix}/transcription/capture/processes"),
            (HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{sessionId}/capture/process"),
            (HttpMethod.Delete, $"{ApiPrefix}/transcription/sessions/{sessionId}/capture/process")
        ];

        await using var factory = FactoryWith(new NotSupportedProcessAudioCaptureSource());
        using var client = factory.CreateClient();

        foreach (var (method, route) in routes)
        {
            using var forbidden = new HttpRequestMessage(method, route);
            if (method == HttpMethod.Post)
            {
                forbidden.Content = JsonContent.Create(new
                {
                    processId = 4321
                });
            }

            factory.AddNonOperatorBearerToken(forbidden);
            using var forbiddenResponse = await client.SendAsync(forbidden);
            AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode, $"'{method} {route}' must refuse a non-operator.");

            // The control: without it a route broken for everyone would pass the assertion above.
            using var allowed = new HttpRequestMessage(method, route);
            if (method == HttpMethod.Post)
            {
                allowed.Content = JsonContent.Create(new
                {
                    processId = 4321
                });
            }

            factory.AddNodeBearerToken(allowed);
            using var allowedResponse = await client.SendAsync(allowed);
            AssertEx.True(allowedResponse.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                $"'{method} {route}' must admit an operator (got {(int)allowedResponse.StatusCode}).");
        }
    }

    [Test]
    public async Task CaptureRoutes_Anonymous_Return401()
    {
        // Kept alongside the operator proof, never instead of it.
        await using var factory = FactoryWith(new NotSupportedProcessAudioCaptureSource());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ApiPrefix}/transcription/capture/processes");

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task ListCaptureProcesses_OnUnsupportedHost_ReturnsSupportedFalseAndEmptyList()
    {
        // 200 with supported:false, never 404: the SPA needs to tell "this OS cannot" apart from a routing mistake,
        // and an empty list alone cannot say which.
        await using var factory = FactoryWith(new NotSupportedProcessAudioCaptureSource());
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/capture/processes");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.False(body.GetProperty("supported").GetBoolean(), "A host without process loopback reports the capability as false.");
        AssertEx.Equal(0, body.GetProperty("processes").GetArrayLength(), "There is nothing to offer, and that is not an error.");
    }

    [Test]
    public async Task ListCaptureProcesses_OnASupportedHost_ReturnsTheCandidates()
    {
        // The control for the test above: without it a picker broken for everyone would report supported:false.
        var source = new StubProcessAudioCaptureSource
        {
            IsSupported = true,
            Candidates = [new ProcessAudioCaptureCandidate { ProcessId = 1234, Name = "chrome", HasAudio = true }]
        };
        await using var factory = FactoryWith(source);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/capture/processes");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.True(body.GetProperty("supported").GetBoolean(), "A supported host says so.");
        var first = body.GetProperty("processes")[0];
        AssertEx.Equal(1234, first.GetProperty("pid").GetInt32());
        AssertEx.Equal("chrome", first.GetProperty("name").GetString());
        AssertEx.True(first.GetProperty("hasAudio").GetBoolean());
    }

    [Test]
    public async Task StartProcessCapture_OnUnsupportedHost_ReturnsTypedBadRequest()
    {
        // 400 rather than 409: no retry could ever succeed on this host.
        await using var factory = FactoryWith(new NotSupportedProcessAudioCaptureSource());
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}/capture/process");
        request.Content = JsonContent.Create(new
        {
            processId = 4321
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("capture-not-supported", body.GetProperty("reason").GetString(),
            "The SPA branches on the reason code, not on the prose.");
    }

    [Test]
    public async Task StartProcessCapture_BeforeLiveStart_ReturnsTypedConflict()
    {
        // The call order is create, subscribe, live/start, then this. Without the live start the session has no
        // lanes, so a recorder would capture audio with nowhere to put it.
        await using var factory = FactoryWith(new StubProcessAudioCaptureSource
        {
            IsSupported = true
        });
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}/capture/process");
        request.Content = JsonContent.Create(new
        {
            processId = 4321
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("session-not-live", body.GetProperty("reason").GetString(),
            "The caller can fix this one and retry, which is what separates it from the 400 above.");
    }

    [Test]
    public async Task StartProcessCapture_WithANonPositiveProcessId_IsRejected()
    {
        await using var factory = FactoryWith(new StubProcessAudioCaptureSource
        {
            IsSupported = true
        });
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}/capture/process");
        request.Content = JsonContent.Create(new
        {
            processId = 0
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode,
            "WithProcessLoopback takes a uint, so a zero or negative id is never a process that could be captured.");
    }

    [Test]
    public async Task StopProcessCapture_WhenNothingIsCapturing_Returns404()
    {
        await using var factory = FactoryWith(new StubProcessAudioCaptureSource
        {
            IsSupported = true
        });
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Delete, $"{ApiPrefix}/transcription/sessions/{Guid.NewGuid()}/capture/process");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "There was no capture to stop.");
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task RuntimeStatus_ReportsProcessCaptureSupported(bool processCaptureSupported, bool vadInstalled)
    {
        // The two flags are adjacent booleans on the view, so the arguments are deliberately opposite: a mapper
        // that swapped them would report the VAD state as the capture capability and still pass a same-value test.
        var runtime = new StubTranscriptionRuntimeService
        {
            Runtime = new TranscriptionRuntimeView
            {
                Enabled = true,
                Runtime = new WhisperRuntimeStatusSnapshot(WhisperRuntimeState.Stopped, null, null, null, null, SupportsTranscode: true),
                Activity = new WhisperRuntimeActivitySnapshot(ActiveTranscriptionCount: 0, SpawnReadinessCount: 0, ResidentProcessCount: 0, MutationReserved: false, EvictionReserved: false),
                ManagedRuntime = null,
                SelectedModelId = null,
                RecommendedModelId = "base",
                IdleTimeoutMinutes = 15,
                VadInstalled = vadInstalled,
                ProcessCaptureSupported = processCaptureSupported
            }
        };

        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITranscriptionRuntimeService>();
                services.AddSingleton<ITranscriptionRuntimeService>(runtime);
            }
        };
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Get, $"{ApiPrefix}/transcription/runtime");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal(processCaptureSupported, body.GetProperty("processCaptureSupported").GetBoolean(),
            "The SPA gates the per-application source on this flag.");
        AssertEx.Equal(vadInstalled, body.GetProperty("vadInstalled").GetBoolean(),
            "The neighbouring flag is unchanged, which is what rules out a swap.");
    }

    [Test]
    public async Task StartProcessCapture_OnALiveSession_Returns200AndTheStopReturns204()
    {
        // The Started arm and the stop that actually stopped something: both are the wire shape the SPA drives, and
        // neither was reachable without a genuinely live session.
        await using var factory = FactoryWith(new StubProcessAudioCaptureSource
        {
            IsSupported = true
        });
        using var client = factory.CreateClient();
        var sessionId = await RegisterLiveSessionAsync(factory);
        var route = $"{ApiPrefix}/transcription/sessions/{sessionId}/capture/process";

        using var start = Authorized(factory, HttpMethod.Post, route);
        start.Content = JsonContent.Create(new
        {
            processId = 4321
        });
        using var started = await client.SendAsync(start);

        AssertEx.Equal(HttpStatusCode.OK, started.StatusCode);
        var body = await started.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal(sessionId.ToString(), body.GetProperty("sessionId").GetString(), "The answer names the session it attached to.");
        AssertEx.True(body.GetProperty("capturing").GetBoolean(), "Capture is running.");

        using var stop = Authorized(factory, HttpMethod.Delete, route);
        using var stopped = await client.SendAsync(stop);

        AssertEx.Equal(HttpStatusCode.NoContent, stopped.StatusCode, "A stop that stopped something answers 204, not 404.");
    }

    [Test]
    public async Task StartProcessCapture_WhenAlreadyCapturing_ReturnsTypedConflict()
    {
        // Two recorders on one session would interleave two audio clocks into one lane. The reason code is what the
        // SPA branches on to say "stop the current capture first".
        await using var factory = FactoryWith(new StubProcessAudioCaptureSource
        {
            IsSupported = true
        });
        using var client = factory.CreateClient();
        var sessionId = await RegisterLiveSessionAsync(factory);
        var route = $"{ApiPrefix}/transcription/sessions/{sessionId}/capture/process";

        using var first = Authorized(factory, HttpMethod.Post, route);
        first.Content = JsonContent.Create(new
        {
            processId = 4321
        });
        using var firstResponse = await client.SendAsync(first);
        AssertEx.Equal(HttpStatusCode.OK, firstResponse.StatusCode, "The control: the first start must succeed, or the conflict below proves nothing.");

        using var second = Authorized(factory, HttpMethod.Post, route);
        second.Content = JsonContent.Create(new
        {
            processId = 9876
        });
        using var secondResponse = await client.SendAsync(second);

        AssertEx.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);
        var body = await secondResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("capture-already-running", body.GetProperty("reason").GetString(),
            "A second start is refused, not queued, and says which of the two conflicts it is.");

        using var stop = Authorized(factory, HttpMethod.Delete, route);
        using var stopped = await client.SendAsync(stop);
        AssertEx.Equal(HttpStatusCode.NoContent, stopped.StatusCode, "Left tidy so the host's shutdown has nothing to drain.");
    }

    /// <summary>
    ///     Makes a session live through the REAL registry, which is what <c>StartProcessCaptureEndpoint</c> asks.
    ///     <c>Persist = false</c> is the dictation shape: it needs no session row, so this stays an endpoint test
    ///     rather than dragging the store and the batch writer in behind it.
    /// </summary>
    private static async Task<Guid> RegisterLiveSessionAsync(TestServerWebAppFactory factory)
    {
        var registry = factory.Services.GetRequiredService<ILiveTranscriptionSessionRegistry>();
        var sessionId = Guid.NewGuid();
        await registry.StartLiveSessionAsync(sessionId,
                          new LiveSessionOptions
                          {
                              ModelId = "base",
                              Settings = new LiveSegmenterSettings(),
                              Channels = [TranscriptChannel.Others],
                              SourceKind = TranscriptionSourceKind.ApplicationProcess,
                              Persist = false
                          },
                          CancellationToken.None);
        return sessionId;
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, route);
        factory.AddNodeBearerToken(request);
        return request;
    }

    private static TestServerWebAppFactory FactoryWith(IProcessAudioCaptureSource source) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                // Only the source is replaced: ProcessAudioCaptureCoordinator resolves it from the container, so
                // the real coordinator is what answers these routes.
                services.RemoveAll<IProcessAudioCaptureSource>();
                services.AddSingleton(source);
            }
        };

    /// <summary>A capture source whose capability and candidate list the test sets, and which never captures.</summary>
    private sealed class StubProcessAudioCaptureSource : IProcessAudioCaptureSource
    {
        public bool IsSupported { get; init; }

        public IReadOnlyList<ProcessAudioCaptureCandidate> Candidates { get; init; } = [];

        public ValueTask<IReadOnlyList<ProcessAudioCaptureCandidate>> ListCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Candidates);

        public async Task CaptureAsync(Guid sessionId, int processId, CancellationToken cancellationToken)
        {
            // Runs until the coordinator cancels, which is what the real source does. Throwing instead would let
            // the coordinator remove the entry and the already-capturing arm could never be observed. A token
            // registration, not a timer, so nothing here waits on the clock.
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => stopped.TrySetResult()))
            {
                await stopped.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The runtime facade, stubbed so the status route touches no hardware probe and no data directory.</summary>
    private sealed class StubTranscriptionRuntimeService : ITranscriptionRuntimeService
    {
        public required TranscriptionRuntimeView Runtime { get; init; }

        public Task<TranscriptionRuntimeView> GetRuntimeAsync(CancellationToken ct) =>
            Task.FromResult(Runtime);

        public Task<WhisperServerEvictResult> EjectAsync(CancellationToken ct) =>
            throw new NotSupportedException("Not exercised here.");

        public Task<TranscriptionModelCatalogView> GetModelsAsync(CancellationToken ct) =>
            throw new NotSupportedException("Not exercised here.");

        public Task<WhisperModelEntry> GetRecommendedModelAsync(CancellationToken ct) =>
            throw new NotSupportedException("Not exercised here.");

        public Task<TranscriptionModelCatalogView> SelectModelAsync(string? modelId, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised here.");

        public Task<string> ResolveEffectiveModelIdAsync(CancellationToken ct) =>
            throw new NotSupportedException("Not exercised here.");
    }
}
