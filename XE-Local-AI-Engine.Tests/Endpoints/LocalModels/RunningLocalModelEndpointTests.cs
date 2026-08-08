namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint tests for the loaded-models surface: <c>GET models/running</c> (footprint mapping + graceful-unavailable)
///     and <c>POST models/{modelName}/unload</c> (decode-before-validate, idempotent graceful unload, unsafe-name guard).
/// </summary>
public sealed class RunningLocalModelEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetRunningModels_WhenLoaded_ReturnsModelsWithMemoryFootprint()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        context.Server!.State.RunningModels =
        [
            new FakeOllamaState.FakeOllamaRunningModel("llama3:8b", DateTimeOffset.UtcNow.AddMinutes(5), SizeBytes: 5_000_000_000, SizeVramBytes: 4_000_000_000)
        ];
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/running");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(running.IsAvailable);
        AssertEx.ContainsSingle(running.Items, item => item.ModelName == "llama3:8b");
        var model = running.Items.Single(item => item.ModelName == "llama3:8b");
        AssertEx.Equal(expected: 5_000_000_000L, model.SizeBytes);
        AssertEx.Equal(expected: 4_000_000_000L, model.SizeVramBytes);
        AssertEx.True(model.ExpiresAtUtc.HasValue);
    }

    [Test]
    public async Task GetRunningModels_WhenNoneLoaded_ReturnsAvailableEmpty()
    {
        await using var context = await CreateContextAsync("llama3:8b").ConfigureAwait(false);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/running");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(running.IsAvailable);
        AssertEx.Empty(running.Items);
    }

    [Test]
    public async Task GetRunningModels_WhenProviderUnavailable_ReturnsSafeUnavailableResponse()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.ListRunningModelsAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<IReadOnlyList<RunningModelSnapshot>>>(_ => throw new InvalidOperationException("provider offline"));
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/running");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(running.IsAvailable);
        AssertEx.Empty(running.Items);
        AssertEx.Equal("Local model provider is unavailable.", running.Error);
    }

    [Test]
    public async Task GetRunningModels_WhenOllamaUnreachable_ReturnsSafeUnavailableResponse()
    {
        // Desktop mode has no Ollama endpoint, so ListRunningModelsAsync throws HttpRequestException on every poll.
        // The endpoint must degrade to the same OK-unavailable response as any other provider failure (never a 500).
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.ListRunningModelsAsync(Arg.Any<CancellationToken>())
                    .Returns<Task<IReadOnlyList<RunningModelSnapshot>>>(_ => throw new HttpRequestException("Connection refused"));
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/running");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(running.IsAvailable);
        AssertEx.Empty(running.Items);
        AssertEx.Equal("Local model provider is unavailable.", running.Error);
    }

    [Test]
    public async Task UnloadModel_WhenValid_GracefullyUnloadsAndReportsSuccess()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/llama3:8b/unload");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("llama3:8b", unloaded.ModelName);
        AssertEx.True(unloaded.Unloaded);
        await modelService.Received(1).UnloadModelAsync("llama3:8b", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenNotLoaded_IsIdempotentSuccess()
    {
        // The runtime treats unloading a model it is not holding as a no-op; the endpoint must still report success so the
        // eject action is safe to retry.
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/not-loaded:latest/unload");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(unloaded.Unloaded);
        await modelService.Received(1).UnloadModelAsync("not-loaded:latest", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenNameHasEncodedSlashes_DecodesBeforeUnloading()
    {
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory,
            HttpMethod.Post,
            "/api/local/v1/models/hf.co%2Funsloth%2Fgemma-4-12b-it-GGUF%3AUD-Q4_K_XL/unload");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", unloaded.ModelName);
        await modelService.Received(1)
                          .UnloadModelAsync("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenNameIsUnsafe_ReturnsValidationProblem()
    {
        // ..%2F..%2Fetc decodes to "../../etc", which the validator rejects AFTER decoding — so decoding cannot smuggle
        // path traversal past the guard.
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/hf.co%2F..%2F..%2Fetc/unload");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await modelService.DidNotReceiveWithAnyArgs().UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task<RunningModelEndpointTestContext> CreateContextAsync(params string[] models)
    {
        var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = models.Length > 0 ? models : ["chat"]
        }, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return new RunningModelEndpointTestContext(server);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static RunningModelEndpointTestContext CreateContext(IOllamaModelService modelService)
    {
        return new RunningModelEndpointTestContext(modelService);
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        // The unload route binds its model name from the path, so the body is empty. FastEndpoints 415s a truly empty POST
        // body, so the React client (and these tests) post "{}" — mirror that here for every POST.
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new
            {
            });
        }

        return request;
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

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return Settings;
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class RunningModelEndpointTestContext : IAsyncDisposable
    {
        private readonly OllamaApiClient? _ollamaClient;
        private readonly OllamaModelService? _ownedModelService;

        public RunningModelEndpointTestContext(FakeOllamaServer server)
        {
            Server = server ?? throw new ArgumentNullException(nameof(server));
            _ollamaClient = new OllamaApiClient(Server.BaseAddress);
            _ownedModelService = new OllamaModelService(_ollamaClient);
            Factory = CreateFactory(_ownedModelService);
        }

        public RunningModelEndpointTestContext(IOllamaModelService modelService)
        {
            Factory = CreateFactory(modelService ?? throw new ArgumentNullException(nameof(modelService)));
        }

        public TestingWebAppFactory Factory { get; }

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

        private static TestingWebAppFactory CreateFactory(IOllamaModelService modelService)
        {
            return new TestingWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IOllamaModelService>();
                    services.AddSingleton(modelService);
                    services.RemoveAll<INodeSettingsStore>();
                    services.AddSingleton<INodeSettingsStore>(new StubNodeSettingsStore(new StoredNodeSettings()));
                }
            };
        }
    }
}
