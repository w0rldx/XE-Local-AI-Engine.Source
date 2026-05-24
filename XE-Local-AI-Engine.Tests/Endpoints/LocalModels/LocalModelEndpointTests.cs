namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalModelEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task ListLocalModels_WhenAvailable_ReturnsModelsAndSelection()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        context.SettingsStore.Settings = new StoredNodeSettings { DefaultModelName = "llama3:8b" };
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(models.IsAvailable);
        AssertEx.Equal("llama3:8b", models.SelectedModelName);
        AssertEx.ContainsSingle(models.Items, item => item.ModelName == "llama3:8b");
        var model = models.Items.Single(item => item.ModelName == "llama3:8b");
        AssertEx.True(model.IsSelected);
        AssertEx.Equal("fake", model.Family);
        AssertEx.Equal("Q0_0", model.QuantizationLevel);
    }

    [Test]
    public async Task ListLocalModels_WhenProviderUnavailable_ReturnsSafeUnavailableResponse()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.ListLocalModelsAsync(Arg.Any<CancellationToken>()).Returns<Task<IEnumerable<OllamaSharp.Models.Model>>>(_ => throw new InvalidOperationException("provider offline"));
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()));
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var models = await ReadJsonAsync<ListLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(models.IsAvailable);
        AssertEx.Empty(models.Items);
        AssertEx.Equal("Local model provider is unavailable.", models.Error);
    }

    [Test]
    public async Task GetLocalModelDetails_WhenModelExists_ReturnsSecretSafeDetails()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        context.Server!.State.ModelInfo["llama3:8b"] = new Dictionary<string, object?>
        {
            ["llama.context_length"] = 8192
        };
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/llama3:8b/details");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var details = Deserialize<LocalModelDetailsResponse>(body);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("llama3:8b", details.ModelName);
        AssertEx.Equal(8192, details.MaxContextTokens);
        AssertEx.Equal("{{ .Prompt }}", details.Template);
        AssertEx.False(body.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        AssertEx.Contains(context.Server.RecordedRequests, recorded => recorded.Path == "/api/show" && recorded.ModelName == "llama3:8b");
    }

    [Test]
    public async Task SelectLocalModel_WhenValid_PersistsDefaultModelWithoutProviderSwitching()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        var settingsStore = new StubNodeSettingsStore(new StoredNodeSettings { MaxMessageRequestTimeoutSeconds = 120 });
        await using var context = CreateContext(modelService, settingsStore);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/select");
        request.Content = JsonContent.Create(new SelectLocalModelRequest { ModelName = "llama3:8b" });
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var selection = await ReadJsonAsync<SelectLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("llama3:8b", selection.SelectedModelName);
        AssertEx.Equal("llama3:8b", settingsStore.Settings.DefaultModelName);
        AssertEx.Equal(120, settingsStore.Settings.MaxMessageRequestTimeoutSeconds);
        await modelService.DidNotReceiveWithAnyArgs().ListLocalModelsAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PullAndDeleteLocalModel_WhenValid_UseOllamaModelService()
    {
        await using var context = await CreateContextAsync("chat").ConfigureAwait(false);
        using var client = context.Factory.CreateClient();

        using var pullRequest = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/pull");
        pullRequest.Content = JsonContent.Create(new PullLocalModelRequest { ModelName = "orca-mini:latest" });
        using var pullResponse = await client.SendAsync(pullRequest).ConfigureAwait(false);
        var pull = await ReadJsonAsync<PullLocalModelResponse>(pullResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, pullResponse.StatusCode);
        AssertEx.Equal("orca-mini:latest", pull.ModelName);
        AssertEx.Contains(context.Server!.State.Models, "orca-mini:latest");
        AssertEx.Contains(context.Server.RecordedRequests, request => request.Path == "/api/pull" && request.ModelName == "orca-mini:latest");

        using var deleteRequest = CreateRequest(context.Factory, HttpMethod.Delete, "/api/local/v1/models/orca-mini:latest");
        using var deleteResponse = await client.SendAsync(deleteRequest).ConfigureAwait(false);
        var deleted = await ReadJsonAsync<DeleteLocalModelResponse>(deleteResponse).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        AssertEx.Equal("orca-mini:latest", deleted.ModelName);
        AssertEx.True(deleted.Deleted);
        AssertEx.False(context.Server.State.Models.Contains("orca-mini:latest"));
        AssertEx.Contains(context.Server.RecordedRequests, request => request.Path == "/api/delete" && request.ModelName == "orca-mini:latest");
    }

    [Test]
    public async Task PullLocalModel_WhenModelNameIsUnsafe_ReturnsValidationProblem()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService, new StubNodeSettingsStore(new StoredNodeSettings()));
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/pull");
        request.Content = JsonContent.Create(new PullLocalModelRequest { ModelName = "../secret" });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        modelService.DidNotReceiveWithAnyArgs().PullModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task<LocalModelEndpointTestContext> CreateContextAsync(params string[] models)
    {
        var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = models.Length > 0 ? models : ["chat"]
        }, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return new LocalModelEndpointTestContext(server);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static LocalModelEndpointTestContext CreateContext(IOllamaModelService modelService, StubNodeSettingsStore settingsStore)
    {
        return new LocalModelEndpointTestContext(modelService, settingsStore);
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        var token = factory.Services.GetRequiredService<ILocalOperatorTokenProvider>().Token;
        request.Headers.Add(LocalOperatorAuthorization.HeaderName, token);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static T Deserialize<T>(string json)
        where T : class
    {
        return AssertEx.NotNull(JsonSerializer.Deserialize<T>(json, JsonOptions));
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }

    private sealed class StubNodeSettingsStore(StoredNodeSettings settings) : INodeSettingsStore
    {
        public StoredNodeSettings Settings { get; set; } = settings;

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class LocalModelEndpointTestContext : IAsyncDisposable
    {
        private readonly OllamaApiClient? _ollamaClient;
        private readonly OllamaModelService? _ownedModelService;

        public LocalModelEndpointTestContext(FakeOllamaServer server)
        {
            Server = server ?? throw new ArgumentNullException(nameof(server));
            _ollamaClient = new OllamaApiClient(Server.BaseAddress);
            _ownedModelService = new OllamaModelService(_ollamaClient);
            SettingsStore = new StubNodeSettingsStore(new StoredNodeSettings());
            Factory = CreateFactory(_ownedModelService, SettingsStore);
        }

        public LocalModelEndpointTestContext(IOllamaModelService modelService, StubNodeSettingsStore settingsStore)
        {
            SettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
            Factory = CreateFactory(modelService ?? throw new ArgumentNullException(nameof(modelService)), SettingsStore);
        }

        public TestingWebAppFactory Factory { get; }

        public StubNodeSettingsStore SettingsStore { get; }

        public FakeOllamaServer? Server { get; }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync().ConfigureAwait(false);

            _ownedModelService?.Dispose();
            _ollamaClient?.Dispose();

            if (Server is not null)
            {
                await Server.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static TestingWebAppFactory CreateFactory(IOllamaModelService modelService, StubNodeSettingsStore settingsStore)
        {
            return new TestingWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IOllamaModelService>();
                    services.AddSingleton(modelService);
                    services.RemoveAll<INodeSettingsStore>();
                    services.AddSingleton<INodeSettingsStore>(settingsStore);
                }
            };
        }
    }
}
