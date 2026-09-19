namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The three runtime routes. The one that earns the most attention is the eject: a refusal has to come back as a
///     409 carrying the activity snapshot, because that is what lets the operator see whether to wait or to retry.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptionRuntimeEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetStatus_ReportsStateBackendSelectedAndRecommendedModel()
    {
        var service = new StubTranscriptionRuntimeService
        {
            Runtime = new TranscriptionRuntimeView
            {
                Enabled = true,
                Runtime = new WhisperRuntimeStatusSnapshot
                {
                    State = WhisperRuntimeState.Ready,
                    LoadedModelId = "base",
                    Backend = WhisperBackend.Cuda,
                    BinaryVersion = "b5130",
                    BinarySource = WhisperBinarySource.Pinned,
                    SupportsTranscode = true
                },
                Activity = new WhisperRuntimeActivitySnapshot { ActiveTranscriptionCount = 0, SpawnReadinessCount = 0, ResidentProcessCount = 1, MutationReserved = false, EvictionReserved = false },
                ManagedRuntime = null,
                SelectedModelId = "large-v3-turbo",
                RecommendedModelId = "large-v3-turbo-q8_0",
                IdleTimeoutMinutes = 20,
                VadInstalled = true,
                ProcessCaptureSupported = false
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime");

        AssertEx.Equal("ready", body.GetProperty("state").GetString());
        AssertEx.Equal("cuda", body.GetProperty("backend").GetString());
        AssertEx.Equal("pinned", body.GetProperty("binarySource").GetString());
        AssertEx.Equal("base", body.GetProperty("loadedModelId").GetString());
        AssertEx.Equal("large-v3-turbo", body.GetProperty("selectedModelId").GetString());
        AssertEx.Equal("large-v3-turbo-q8_0", body.GetProperty("recommendedModelId").GetString());
        AssertEx.Equal(expected: 20, body.GetProperty("idleTimeoutMinutes").GetInt32());
        AssertEx.True(body.GetProperty("enabled").GetBoolean());
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GetStatus_ReportsWhetherTheVadWeightsAreInstalled(bool vadInstalled)
    {
        // The daemon always launches with voice-activity detection on, so a node missing these weights cannot start
        // the runtime at all. Without this field the operator sees only a failed start and no cause.
        var service = new StubTranscriptionRuntimeService
        {
            Runtime = StoppedRuntime() with
            {
                VadInstalled = vadInstalled
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime");

        AssertEx.Equal(vadInstalled, body.GetProperty("vadInstalled").GetBoolean());
    }

    [Test]
    public async Task GetStatus_ReportsSupportsTranscodeFromTheFfmpegProbe()
    {
        // The flag describes an ENGINE capability — transcoding containers the daemon cannot decode — and never a
        // daemon flag, so it rides the status rather than changing how the daemon is launched.
        var service = new StubTranscriptionRuntimeService
        {
            Runtime = StoppedRuntime() with
            {
                Runtime = new WhisperRuntimeStatusSnapshot { State = WhisperRuntimeState.Stopped, LoadedModelId = null, Backend = null, BinaryVersion = null, BinarySource = null, SupportsTranscode = false }
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime");

        AssertEx.False(body.GetProperty("supportsTranscode").GetBoolean());
    }

    [Test]
    public async Task GetStatus_WithNoManagedRuntimeRecord_ReportsManagedRuntimeNull()
    {
        // The field is on the contract from the first slice so the generated client's shape does not change under it
        // later; a node that has never adopted a build reports null here.
        await using var factory = FactoryWith(new StubTranscriptionRuntimeService());
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime");

        AssertEx.Equal(JsonValueKind.Null, body.GetProperty("managedRuntime").ValueKind);
    }

    [Test]
    public async Task GetStatus_ReportsTheManagedRuntimeOnceARecordExists()
    {
        // The other half of the field above, reachable now that the managed source-build lane can write a record.
        // Every member is asserted on the wire because this is what the operator UI reads to say WHICH runtime a node
        // is serving from — and the enums must arrive as their camel-case tokens, not as .NET member names.
        var installedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_726_000_000_000);
        var service = new StubTranscriptionRuntimeService
        {
            Runtime = StoppedRuntime() with
            {
                ManagedRuntime = new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Active,
                    WhisperBackend.Cuda,
                    WhisperCppSourceBuildRequestValidation.OfficialRepository,
                    WhisperCppReleasePins.PinnedSourceCommitSha,
                    WhisperCppSourceSelection.Official,
                    WhisperCppSourceRevisionMode.EnginePinned,
                    SourceRequestedCommit: null,
                    "/managed/cuda/whisper.cpp/bin",
                    new string(c: 'a', count: 64),
                    installedAt)
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime");

        var managed = body.GetProperty("managedRuntime");
        AssertEx.Equal("active", managed.GetProperty("validity").GetString());
        AssertEx.Equal("cuda", managed.GetProperty("desiredBackend").GetString());
        AssertEx.Equal("official", managed.GetProperty("sourceSelection").GetString());
        AssertEx.Equal("enginePinned", managed.GetProperty("sourceRevisionMode").GetString());
        AssertEx.Equal(WhisperCppSourceBuildRequestValidation.OfficialRepository, managed.GetProperty("sourceRepository").GetString());
        AssertEx.Equal(WhisperCppReleasePins.PinnedSourceCommitSha, managed.GetProperty("sourceCommit").GetString());
        AssertEx.Equal(installedAt.ToUnixTimeMilliseconds(), managed.GetProperty("installedAtUtc").GetInt64());

        // The server digest and the on-disk path are deliberately NOT on the wire: neither tells the operator
        // anything, and the path is a filesystem detail the SPA has no business carrying.
        AssertEx.False(managed.TryGetProperty("serverSha256", out _), "The record's digest must not be published.");
        AssertEx.False(managed.TryGetProperty("sourceBuildPath", out _), "The record's on-disk path must not be published.");
    }

    [Test]
    public async Task Eject_WhileATranscriptionIsRunning_Returns409RuntimeBusyWithTheActivitySnapshot()
    {
        var busy = new WhisperRuntimeActivitySnapshot { ActiveTranscriptionCount = 1, SpawnReadinessCount = 0, ResidentProcessCount = 1, MutationReserved = false, EvictionReserved = false };
        var service = new StubTranscriptionRuntimeService
        {
            EvictResult = new WhisperServerEvictResult { Evicted = false, Activity = busy }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/eject", new
        {
            accepted = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.Equal("runtime-busy", body.GetProperty("reason").GetString());
        // The snapshot is the point: "busy" alone does not tell the operator whether to wait or to retry.
        AssertEx.Equal(expected: 1, body.GetProperty("activity").GetProperty("activeTranscriptionCount").GetInt32());
        AssertEx.True(body.GetProperty("activity").GetProperty("isBusy").GetBoolean());
    }

    [Test]
    public async Task Eject_WhenIdle_Returns200AndAnEmptyActivitySnapshot()
    {
        var service = new StubTranscriptionRuntimeService
        {
            EvictResult = new WhisperServerEvictResult
            {
                Evicted = true,
                Activity = new WhisperRuntimeActivitySnapshot { ActiveTranscriptionCount = 0, SpawnReadinessCount = 0, ResidentProcessCount = 0, MutationReserved = false, EvictionReserved = false }
            }
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/runtime/eject", new
        {
            accepted = true
        });
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        AssertEx.False(body.GetProperty("activity").GetProperty("isBusy").GetBoolean());
        AssertEx.Equal(expected: 1, service.EjectCallCount);
    }

    [Test]
    public async Task GetRecommendation_ReturnsTheCatalogueRowForTheEffectiveProfile()
    {
        var service = new StubTranscriptionRuntimeService
        {
            Recommended = AssertEx.NotNull(WhisperModelCatalog.Find("large-v3-turbo-q8_0"))
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/runtime/recommendation");

        AssertEx.Equal("large-v3-turbo-q8_0", body.GetProperty("recommendedModelId").GetString());
        AssertEx.Equal("LargeTurbo", body.GetProperty("tier").GetString());
        AssertEx.True(body.GetProperty("approximateVramBytes").GetInt64() > 0,
            "The footprint figures are what let the operator see the reasoning behind the pick.");
    }

    [Test]
    [Arguments("GET", "transcription/runtime")]
    [Arguments("GET", "transcription/runtime/recommendation")]
    [Arguments("POST", "transcription/runtime/eject")]
    public async Task RuntimeRoutes_WithAnAuthenticatedNonOperator_Return403(string method, string route)
    {
        // Operator gating is proven with an AUTHENTICATED non-operator, never with an anonymous 401: swapping the
        // operator policy for plain authentication would keep a 401 assertion green while the route became open to
        // every signed-in caller.
        await using var factory = FactoryWith(new StubTranscriptionRuntimeService());
        using var client = factory.CreateClient();

        using var forbidden = new HttpRequestMessage(new HttpMethod(method), $"{ApiPrefix}/{route}");
        if (method == "POST")
        {
            forbidden.Content = JsonContent.Create(new
            {
                accepted = true
            });
        }

        factory.AddNonOperatorBearerToken(forbidden);
        using var forbiddenResponse = await client.SendAsync(forbidden);

        AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode,
            $"'{route}' must refuse an authenticated non-operator.");

        // The control: the same route must NOT 403 for an operator, or the assertion above would pass against a
        // route that is simply broken.
        using var allowed = new HttpRequestMessage(new HttpMethod(method), $"{ApiPrefix}/{route}");
        if (method == "POST")
        {
            allowed.Content = JsonContent.Create(new
            {
                accepted = true
            });
        }

        factory.AddNodeBearerToken(allowed);
        using var allowedResponse = await client.SendAsync(allowed);

        AssertEx.NotEqual(HttpStatusCode.Forbidden, allowedResponse.StatusCode,
            $"'{route}' must admit an operator.");
    }

    [Test]
    public async Task RuntimeRoutes_Anonymous_Return401()
    {
        // Kept alongside the operator proof, never instead of it.
        await using var factory = FactoryWith(new StubTranscriptionRuntimeService());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ApiPrefix}/transcription/runtime");

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<JsonElement> GetJsonAsync(TestServerWebAppFactory factory, HttpClient client, string route)
    {
        using var request = Authorized(factory, HttpMethod.Get, route, body: null);
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"'{route}' must answer 200 for an operator.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
    }

    private static HttpRequestMessage Authorized(TestServerWebAppFactory factory, HttpMethod method, string route, object? body)
    {
        var request = new HttpRequestMessage(method, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        factory.AddNodeBearerToken(request);
        return request;
    }

    private static TestServerWebAppFactory FactoryWith(ITranscriptionRuntimeService service) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITranscriptionRuntimeService>();
                services.AddSingleton(service);
            }
        };

    private static TranscriptionRuntimeView StoppedRuntime() =>
        new()
        {
            Enabled = true,
            Runtime = new WhisperRuntimeStatusSnapshot { State = WhisperRuntimeState.Stopped, LoadedModelId = null, Backend = null, BinaryVersion = null, BinarySource = null, SupportsTranscode = true },
            Activity = new WhisperRuntimeActivitySnapshot { ActiveTranscriptionCount = 0, SpawnReadinessCount = 0, ResidentProcessCount = 0, MutationReserved = false, EvictionReserved = false },
            ManagedRuntime = null,
            SelectedModelId = null,
            RecommendedModelId = "base",
            IdleTimeoutMinutes = 15,
            VadInstalled = true,
            ProcessCaptureSupported = false
        };

    private sealed class StubTranscriptionRuntimeService : ITranscriptionRuntimeService
    {
        public TranscriptionRuntimeView Runtime { get; init; } = StoppedRuntime();

        public WhisperServerEvictResult EvictResult { get; init; } =
            new() { Evicted = true, Activity = new WhisperRuntimeActivitySnapshot { ActiveTranscriptionCount = 0, SpawnReadinessCount = 0, ResidentProcessCount = 0, MutationReserved = false, EvictionReserved = false } };

        public WhisperModelEntry Recommended { get; init; } = WhisperModelCatalog.Models[0];

        public int EjectCallCount { get; private set; }

        public Task<TranscriptionRuntimeView> GetRuntimeAsync(CancellationToken ct) =>
            Task.FromResult(Runtime);

        public Task<WhisperServerEvictResult> EjectAsync(CancellationToken ct)
        {
            EjectCallCount++;
            return Task.FromResult(EvictResult);
        }

        public Task<TranscriptionModelCatalogView> GetModelsAsync(CancellationToken ct) =>
            Task.FromResult(new TranscriptionModelCatalogView { Models = [], SelectedModelId = null, RecommendedModelId = Recommended.Id });

        public Task<WhisperModelEntry> GetRecommendedModelAsync(CancellationToken ct) =>
            Task.FromResult(Recommended);

        public Task<TranscriptionModelCatalogView> SelectModelAsync(string? modelId, CancellationToken ct) =>
            Task.FromResult(new TranscriptionModelCatalogView { Models = [], SelectedModelId = modelId, RecommendedModelId = Recommended.Id });

        public Task<string> ResolveEffectiveModelIdAsync(CancellationToken ct) =>
            Task.FromResult(Recommended.Id);
    }
}
