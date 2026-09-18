namespace XE_Local_AI_Engine.Tests.Endpoints.LocalModels;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint tests for the loaded-models surface: <c>GET models/running</c> (footprint mapping + graceful-unavailable)
///     and <c>POST models/{modelName}/unload</c> (decode-before-validate, idempotent graceful unload, unsafe-name guard,
///     and the eviction of a model from BOTH local runtimes, since the node cannot know which one holds it).
/// </summary>
[Category(TestCategories.Integration)]
public sealed class RunningLocalModelEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task GetRunningModels_WhenLoaded_ReturnsModelsWithMemoryFootprint()
    {
        await using var context = await CreateContextAsync("llama3:8b");
        context.Server!.State.RunningModels =
        [
            new FakeOllamaState.FakeOllamaRunningModel("llama3:8b", DateTimeOffset.UtcNow.AddMinutes(5), SizeBytes: 5_000_000_000, SizeVramBytes: 4_000_000_000)
        ];
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/running");
        using var response = await client.SendAsync(request);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response);

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
        await using var context = await CreateContextAsync("llama3:8b");
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Get, "/api/local/v1/models/running");
        using var response = await client.SendAsync(request);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response);

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
        using var response = await client.SendAsync(request);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response);

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
        using var response = await client.SendAsync(request);
        var running = await ReadJsonAsync<RunningLocalModelsResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(running.IsAvailable);
        AssertEx.Empty(running.Items);
        AssertEx.Equal("Local model provider is unavailable.", running.Error);
    }

    [Test]
    public async Task UnloadModel_WithNoBodyAndNoContentType_IsAcceptedRatherThan415()
    {
        // Regression: the route-only POST is called by the generated client with NO body and therefore NO Content-Type.
        // Without `Description(x => x.Accepts<UnloadLocalModelRequest>())` FastEndpoints' default Accepts metadata
        // (application/json only) rejects that with 415, and the operator gets an empty error toast on every eject.
        // Sending "{}" instead is not a workaround: the generated requestValidator types this body as `never`.
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local/v1/models/llama3:8b/unload");
        context.Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        AssertEx.Null(request.Content);

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await modelService.Received(1).UnloadModelAsync("llama3:8b", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenAnonymousAndBodyLess_ReturnsUnauthorized()
    {
        // Auth runs before content negotiation: a body-less eject from an unauthenticated caller must be 401, never the
        // 415 that would mask the missing token (nor a 200).
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService);
        using var client = context.Factory.CreateClient();

        using var response = await client.PostAsync("/api/local/v1/models/llama3:8b/unload", content: null);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await modelService.DidNotReceiveWithAnyArgs().UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        using var response = await client.SendAsync(request);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response);

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
        using var response = await client.SendAsync(request);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", unloaded.ModelName);
        await modelService.Received(1)
                          .UnloadModelAsync("hf.co/unsloth/gemma-4-12b-it-GGUF:UD-Q4_K_XL", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenTheModelIsOllamaResident_EvictsItFromOllama()
    {
        // The loaded-models page lists what Ollama reports through /api/ps, so its eject always targets an
        // Ollama-resident model. Regression: routing this action by the per-model provider map sent that model to the
        // llama-server supervisor, which held no process for it, so three no-op ejects reported success while Ollama
        // still held the weights and the row never disappeared.
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService, NotRunningSupervisor());
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/llama3:8b/unload");
        using var response = await client.SendAsync(request);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(unloaded.Unloaded);
        await modelService.Received(1).UnloadModelAsync("llama3:8b", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenTheModelIsLlamaServerResident_EjectsEveryRoleGracefully()
    {
        // The other half of the same defect: an unreachable Ollama daemon is the desktop default, and posting
        // keep_alive=0 to an absent 11434 is a connection refused that used to surface as a 500 — so a GGUF model could
        // never be ejected at all, and edited launch arguments needed a full host restart to take effect.
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns<Task>(_ => throw new HttpRequestException("Connection refused"));
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EjectAsync(Arg.Any<string>(), ModelRole.Chat, Arg.Any<bool>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(LlamaServerEjectOutcome.Ejected));
        supervisor.EjectAsync(Arg.Any<string>(), Arg.Is<ModelRole>(role => role != ModelRole.Chat), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(LlamaServerEjectOutcome.NotRunning));
        await using var context = CreateContext(modelService, supervisor);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/qwen3:8b/unload");
        using var response = await client.SendAsync(request);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.Equal("qwen3:8b", unloaded.ModelName);
        AssertEx.True(unloaded.Unloaded);

        // Every role, and never forced: a graceful eject leaves an in-flight turn to drain instead of killing it.
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            await supervisor.Received(1).EjectAsync("qwen3:8b", role, force: false, Arg.Any<CancellationToken>());
        }

        await supervisor.DidNotReceive().EjectAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), force: true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenTheOllamaRuntimeIsDisabled_NeverAsksTheDaemon()
    {
        // A node with the optional runtime switched off has no daemon to ask, so the second half of the eviction is
        // skipped rather than attempted and swallowed.
        var modelService = Substitute.For<IOllamaModelService>();
        await using var context = CreateContext(modelService, NotRunningSupervisor(), ollamaRuntimeEnabled: false);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/qwen3:8b/unload");
        using var response = await client.SendAsync(request);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(unloaded.Unloaded);
        await modelService.DidNotReceiveWithAnyArgs().UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await context.Supervisor.Received(1).EjectAsync("qwen3:8b", ModelRole.Chat, force: false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModel_WhenOllamaAnswersWithAFailureStatus_DoesNotReportSuccess()
    {
        // The negative control for the transport-refusal guard above. A refused CONNECTION carries no status code and
        // means nothing is resident; a daemon that ANSWERS 500 is a live daemon failing a real eviction, so it must not
        // be swallowed into a 200 that tells the operator the VRAM is free.
        var modelService = Substitute.For<IOllamaModelService>();
        modelService.UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns<Task>(_ => throw new HttpRequestException("Ollama failed the eviction",
                        inner: null,
                        HttpStatusCode.InternalServerError));
        await using var context = CreateContext(modelService, NotRunningSupervisor());
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/llama3:8b/unload");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Test]
    public async Task UnloadModel_WhenALlamaServerProcessStaysBusy_ReportsNotUnloaded()
    {
        // The graceful eject leaves a process that did not drain within the window RUNNING. Reporting Unloaded=true
        // there would tell the operator the VRAM is free and the next spawn will carry new launch arguments; neither is
        // true, so the honest answer is a 200 with Unloaded=false.
        var modelService = Substitute.For<IOllamaModelService>();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EjectAsync(Arg.Any<string>(), ModelRole.Chat, Arg.Any<bool>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(LlamaServerEjectOutcome.TimedOutStillBusy));
        supervisor.EjectAsync(Arg.Any<string>(), Arg.Is<ModelRole>(role => role != ModelRole.Chat), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(LlamaServerEjectOutcome.NotRunning));
        await using var context = CreateContext(modelService, supervisor);
        using var client = context.Factory.CreateClient();

        using var request = CreateRequest(context.Factory, HttpMethod.Post, "/api/local/v1/models/qwen3:8b/unload");
        using var response = await client.SendAsync(request);
        var unloaded = await ReadJsonAsync<UnloadLocalModelResponse>(response);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(unloaded.Unloaded);
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
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await modelService.DidNotReceiveWithAnyArgs().UnloadModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task<RunningModelEndpointTestContext> CreateContextAsync(params string[] models)
    {
        var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = models.Length > 0 ? models : ["chat"]
        }, CancellationToken.None);
        try
        {
            return new RunningModelEndpointTestContext(server);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    private static RunningModelEndpointTestContext CreateContext(IOllamaModelService modelService,
        ILlamaServerProcessSupervisor? supervisor = null,
        bool ollamaRuntimeEnabled = true)
    {
        return new RunningModelEndpointTestContext(modelService, supervisor, ollamaRuntimeEnabled);
    }

    /// <summary>A supervisor holding no process for any role — the state of a node whose model is Ollama-resident.</summary>
    private static ILlamaServerProcessSupervisor NotRunningSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EjectAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(LlamaServerEjectOutcome.NotRunning));
        return supervisor;
    }

    private static HttpRequestMessage CreateRequest(TestServerWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");

        // The unload route binds its model name from the path, so no body and no Content-Type ride the request — exactly
        // what the generated client sends (its requestValidator types the body as `never`). Deliberately NOT posting a
        // dummy "{}" here: that would hide a missing Accepts<> override behind a Content-Type the real client never sets.
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions));
    }

    private sealed class StubNodeSettingsStore : INodeSettingsStore
    {
        public StubNodeSettingsStore(StoredNodeSettings settings)
        {
            Settings = settings;
        }

        public StoredNodeSettings Settings { get; set; }

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

        public Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            Settings = mutate(Settings);
            return Task.FromResult(Settings);
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
            Supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
            Factory = CreateFactory(_ownedModelService, Supervisor, ollamaRuntimeEnabled: true);
        }

        public RunningModelEndpointTestContext(IOllamaModelService modelService,
            ILlamaServerProcessSupervisor? supervisor,
            bool ollamaRuntimeEnabled)
        {
            Supervisor = supervisor ?? Substitute.For<ILlamaServerProcessSupervisor>();
            Factory = CreateFactory(modelService ?? throw new ArgumentNullException(nameof(modelService)),
                Supervisor,
                ollamaRuntimeEnabled);
        }

        public TestServerWebAppFactory Factory { get; }

        public FakeOllamaServer? Server { get; }

        public ILlamaServerProcessSupervisor Supervisor { get; }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();

            _ownedModelService?.Dispose();
            _ollamaClient?.Dispose();

            if (Server is not null)
            {
                await Server.DisposeAsync();
            }
        }

        private static TestServerWebAppFactory CreateFactory(IOllamaModelService modelService,
            ILlamaServerProcessSupervisor supervisor,
            bool ollamaRuntimeEnabled)
        {
            return new TestServerWebAppFactory
            {
                AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [OllamaRuntimeGate.RuntimeEnabledConfigurationKey] = ollamaRuntimeEnabled ? "true" : "false"
                },
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<IOllamaModelService>();
                    services.AddSingleton(modelService);
                    services.RemoveAll<ILlamaServerProcessSupervisor>();
                    services.AddSingleton(supervisor);
                    services.RemoveAll<INodeSettingsStore>();
                    services.AddSingleton<INodeSettingsStore>(new StubNodeSettingsStore(new StoredNodeSettings()));
                }
            };
        }
    }
}
