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
///     The four catalogue routes. Two rules earn most of these cases: an unknown model id is rejected at the boundary
///     rather than reaching the coordinator, and a null selection is a VALID request that clears the override rather
///     than a missing field.
/// </summary>
public sealed class TranscriptionModelEndpointTests
{
    private const string ApiPrefix = "/api/local/v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task List_ReportsInstalledSelectedAndRecommended()
    {
        var service = new StubTranscriptionRuntimeService
        {
            Catalog = new TranscriptionModelCatalogView([
                new TranscriptionModelView(Entry("tiny"), Installed: true, Download: null),
                new TranscriptionModelView(Entry("base"), Installed: false,
                    new WhisperModelDownloadStatus("base", WhisperModelDownloadPhase.Running, CompletedBytes: 10, TotalBytes: 100, SanitizedError: null)
                    {
                        PartIndex = 2,
                        PartCount = 2
                    })
            ], "tiny", "large-v3-turbo")
        };
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        var body = await GetJsonAsync(factory, client, $"{ApiPrefix}/transcription/models");

        AssertEx.Equal("tiny", body.GetProperty("selectedModelId").GetString());
        AssertEx.Equal("large-v3-turbo", body.GetProperty("recommendedModelId").GetString());

        var models = body.GetProperty("models");
        AssertEx.Equal(expected: 2, models.GetArrayLength());
        AssertEx.True(models[0].GetProperty("installed").GetBoolean());
        AssertEx.Equal(JsonValueKind.Null, models[0].GetProperty("download").ValueKind);
        AssertEx.Equal("running", models[1].GetProperty("download").GetProperty("phase").GetString());
        AssertEx.Equal(expected: 2, models[1].GetProperty("download").GetProperty("partCount").GetInt32(),
            "A model download is the VAD file plus the weights, and the part counters are what stop a restarting bar looking like a fault.");
    }

    [Test]
    public async Task Download_UnknownModelId_Returns400()
    {
        // Rejected at the boundary: the coordinator's own ArgumentException must never surface as a 500 for what is
        // an ordinary client mistake.
        var coordinator = new StubDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/downloads", new
        {
            modelId = "not-a-model"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Null(coordinator.LastStartedModelId, "An unknown id must never reach the coordinator.");
    }

    [Test]
    public async Task Download_BlankModelId_Returns400()
    {
        var coordinator = new StubDownloadCoordinator();
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/downloads", new
        {
            modelId = "   "
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Null(coordinator.LastStartedModelId);
    }

    [Test]
    public async Task Download_Accepted_ReportsRunning()
    {
        var coordinator = new StubDownloadCoordinator
        {
            Status = new WhisperModelDownloadStatus("base", WhisperModelDownloadPhase.Running, CompletedBytes: null, TotalBytes: null, SanitizedError: null)
        };
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/downloads", new
        {
            modelId = "base"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal("base", body.GetProperty("modelId").GetString());
        AssertEx.True(body.GetProperty("accepted").GetBoolean());
        AssertEx.Equal("running", body.GetProperty("status").GetProperty("phase").GetString());
        AssertEx.Equal("base", coordinator.LastStartedModelId);
    }

    [Test]
    public async Task Download_AlreadyInFlight_RejoinsRatherThanStartingASecondTransfer()
    {
        var coordinator = new StubDownloadCoordinator
        {
            AlreadyInFlight = true
        };
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/downloads", new
        {
            modelId = "base"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.True(body.GetProperty("alreadyInFlight").GetBoolean());
    }

    [Test]
    public async Task DownloadCancel_NothingInFlight_ReportsNoChange()
    {
        // Cancelling a download that just finished is a race, not a mistake, so it is a success reporting false.
        var coordinator = new StubDownloadCoordinator
        {
            CancelResult = false
        };
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/downloads/cancel", new
        {
            modelId = "base"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.False(body.GetProperty("accepted").GetBoolean(), "Nothing was in flight, so nothing may be claimed to have stopped.");
    }

    [Test]
    public async Task DownloadCancel_InFlight_ReachesTheCoordinator()
    {
        var coordinator = new StubDownloadCoordinator
        {
            CancelResult = true
        };
        await using var factory = FactoryWith(coordinator);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/downloads/cancel", new
        {
            modelId = "base"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("base", AssertEx.NotNull(coordinator.LastCancelledModelId));
    }

    [Test]
    public async Task Select_PersistsThroughTheService_AndReturnsTheRefreshedCatalogue()
    {
        var service = new StubTranscriptionRuntimeService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/select", new
        {
            modelId = "small"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal("small", body.GetProperty("selectedModelId").GetString());
        AssertEx.Equal("small", AssertEx.NotNull(service.LastSelectedModelId));
    }

    [Test]
    public async Task Select_NullModelId_ClearsTheOverrideAndFallsBackToTheRecommendation()
    {
        // A null id is a REQUEST, not a missing field: it is how the operator goes back to the recommendation.
        var service = new StubTranscriptionRuntimeService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/select", new
        {
            modelId = (string?)null
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
        AssertEx.Equal(JsonValueKind.Null, body.GetProperty("selectedModelId").ValueKind);
        AssertEx.True(service.SelectCalled, "Clearing the override must still reach the service.");
        AssertEx.Null(service.LastSelectedModelId);
    }

    [Test]
    public async Task Select_UnknownModelId_Returns400()
    {
        var service = new StubTranscriptionRuntimeService();
        await using var factory = FactoryWith(service);
        using var client = factory.CreateClient();

        using var request = Authorized(factory, HttpMethod.Post, $"{ApiPrefix}/transcription/models/select", new
        {
            modelId = "not-a-model"
        });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.False(service.SelectCalled, "An unknown id must be rejected before the service is touched.");
    }

    [Test]
    [Arguments("GET", "transcription/models")]
    [Arguments("POST", "transcription/models/downloads")]
    [Arguments("POST", "transcription/models/downloads/cancel")]
    [Arguments("POST", "transcription/models/select")]
    public async Task ModelRoutes_WithAnAuthenticatedNonOperator_Return403(string method, string route)
    {
        // Proven with an authenticated non-operator and an operator control, never with an anonymous 401: swapping
        // the operator policy for plain authentication would keep a 401 assertion green.
        await using var factory = FactoryWith(new StubTranscriptionRuntimeService());
        using var client = factory.CreateClient();

        using var forbidden = Request(method, route, factory.AddNonOperatorBearerToken);
        using var forbiddenResponse = await client.SendAsync(forbidden).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode,
            $"'{route}' must refuse an authenticated non-operator.");

        using var allowed = Request(method, route, factory.AddNodeBearerToken);
        using var allowedResponse = await client.SendAsync(allowed).ConfigureAwait(false);
        AssertEx.NotEqual(HttpStatusCode.Forbidden, allowedResponse.StatusCode,
            $"'{route}' must admit an operator.");
    }

    private static HttpRequestMessage Request(string method, string route, Action<HttpRequestMessage> authorize)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), $"{ApiPrefix}/{route}");
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new
            {
                modelId = "base"
            });
        }

        authorize(request);
        return request;
    }

    private static WhisperModelEntry Entry(string modelId) =>
        AssertEx.NotNull(WhisperModelCatalog.Find(modelId));

    private static async Task<JsonElement> GetJsonAsync(TestServerWebAppFactory factory, HttpClient client, string route)
    {
        using var request = Authorized(factory, HttpMethod.Get, route, body: null);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, $"'{route}' must answer 200 for an operator.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions).ConfigureAwait(false);
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

    private static TestServerWebAppFactory FactoryWith(IWhisperModelDownloadCoordinator coordinator) =>
        new()
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IWhisperModelDownloadCoordinator>();
                services.AddSingleton(coordinator);
            }
        };

    private sealed class StubDownloadCoordinator : IWhisperModelDownloadCoordinator
    {
        public bool AlreadyInFlight { get; init; }

        public bool CancelResult { get; init; }

        public WhisperModelDownloadStatus? Status { get; init; }

        public string? LastStartedModelId { get; private set; }

        public string? LastCancelledModelId { get; private set; }

        public WhisperModelDownloadTicket Start(string modelId)
        {
            LastStartedModelId = modelId;
            return new WhisperModelDownloadTicket(modelId, AlreadyInFlight);
        }

        public WhisperModelDownloadStatus? GetStatus(string modelId) =>
            Status;

        public IReadOnlyList<WhisperModelDownloadStatus> ListStatuses() =>
            Status is null ? [] : [Status];

        public bool Cancel(string modelId)
        {
            LastCancelledModelId = modelId;
            return CancelResult;
        }
    }

    private sealed class StubTranscriptionRuntimeService : ITranscriptionRuntimeService
    {
        public TranscriptionModelCatalogView Catalog { get; init; } =
            new([], SelectedModelId: null, "base");

        public bool SelectCalled { get; private set; }

        public string? LastSelectedModelId { get; private set; }

        public Task<TranscriptionRuntimeView> GetRuntimeAsync(CancellationToken ct) =>
            Task.FromResult(new TranscriptionRuntimeView(Enabled: true,
                new WhisperRuntimeStatusSnapshot(WhisperRuntimeState.Stopped, null, null, null, null, SupportsTranscode: true),
                new WhisperRuntimeActivitySnapshot(0, 0, 0, MutationReserved: false, EvictionReserved: false),
                ManagedRuntime: null,
                Catalog.SelectedModelId,
                Catalog.RecommendedModelId,
                IdleTimeoutMinutes: 15,
                VadInstalled: true,
                ProcessCaptureSupported: false));

        public Task<WhisperServerEvictResult> EjectAsync(CancellationToken ct) =>
            Task.FromResult(new WhisperServerEvictResult(Evicted: true,
                new WhisperRuntimeActivitySnapshot(0, 0, 0, MutationReserved: false, EvictionReserved: false)));

        public Task<TranscriptionModelCatalogView> GetModelsAsync(CancellationToken ct) =>
            Task.FromResult(Catalog);

        public Task<WhisperModelEntry> GetRecommendedModelAsync(CancellationToken ct) =>
            Task.FromResult(AssertEx.NotNull(WhisperModelCatalog.Find(Catalog.RecommendedModelId)));

        public Task<TranscriptionModelCatalogView> SelectModelAsync(string? modelId, CancellationToken ct)
        {
            SelectCalled = true;
            LastSelectedModelId = modelId;
            return Task.FromResult(Catalog with
            {
                SelectedModelId = modelId
            });
        }

        public Task<string> ResolveEffectiveModelIdAsync(CancellationToken ct) =>
            Task.FromResult(Catalog.RecommendedModelId);
    }
}
