namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using OllamaSharp;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OllamaLocalModelProviderTests
{
    [Test]
    public async Task CheckHealthAsync_WhenFakeOllamaIsRunning_ReturnsHealthy()
    {
        await using var context = await CreateContextAsync();

        var health = await context.Provider.CheckHealthAsync(CancellationToken.None);

        AssertEx.True(health.IsHealthy);
        AssertEx.Equal(OllamaLocalModelProvider.OllamaProviderName, health.ProviderName);
        AssertEx.NotEmpty(health.Diagnostics);
    }

    [Test]
    public async Task ListModelsAsync_WhenModelInfoHasContextLength_ReturnsDescriptors()
    {
        await using var context = await CreateContextAsync("llama3:8b");
        context.Server.State.ModelInfo["llama3:8b"] = new Dictionary<string, object?>
        {
            ["llama.context_length"] = 8192
        };

        var models = await context.Provider.ListModelsAsync(CancellationToken.None);

        AssertEx.ContainsSingle(models, model => model.ModelName == "llama3:8b");
        var descriptor = models.Single(model => model.ModelName == "llama3:8b");
        AssertEx.Equal(OllamaLocalModelProvider.OllamaProviderName, descriptor.ProviderName);
        AssertEx.True(descriptor.IsAvailable);
        AssertEx.Equal(expected: 8192, descriptor.MaxContextTokens);
        AssertEx.Contains(context.Server.RecordedRequests, request => request.Path == "/api/tags");
        AssertEx.Contains(context.Server.RecordedRequests, request => request.Path == "/api/show" && request.ModelName == "llama3:8b");
    }

    [Test]
    public async Task PullModelAsync_WhenFakeOllamaStreamsProgress_ReportsProgressAndAddsModel()
    {
        await using var context = await CreateContextAsync("chat");
        var progressEvents = new List<PullProgress>();
        var progress = new CapturingProgress<PullProgress>(progressEvents);

        await context.Provider.PullModelAsync("orca-mini:latest", progress, CancellationToken.None);

        AssertEx.Contains(context.Server.State.Models, "orca-mini:latest");
        AssertEx.Contains(progressEvents, item => item.Status == "pulling layers" && item.CompletedBytes == 100L && item.TotalBytes == 100L);
        AssertEx.Contains(context.Server.RecordedRequests, request => request.Path == "/api/pull" && request.ModelName == "orca-mini:latest");
    }

    [Test]
    public async Task DeleteModelAsync_WhenModelExists_RemovesModel()
    {
        await using var context = await CreateContextAsync("chat", "delete-me:latest");

        await context.Provider.DeleteModelAsync("delete-me:latest", CancellationToken.None);

        AssertEx.False(context.Server.State.Models.Contains("delete-me:latest"));
        AssertEx.Contains(context.Server.RecordedRequests, request => request.Path == "/api/delete" && request.ModelName == "delete-me:latest");
    }

    [Test]
    public async Task UnloadModelAsync_WhenInvoked_PostsGenerateWithRequestedModelToEvict()
    {
        // The shared client's SelectedModel is "chat"; the eject targets a different model. The fix must send the
        // REQUESTED model (not the client's SelectedModel) to /api/generate so Ollama evicts the right model. The
        // previous OllamaSharp RequestModelUnloadAsync extension recorded "chat" here, which never freed "qwen3:8b".
        await using var context = await CreateContextAsync("chat", "qwen3:8b");

        await context.Provider.UnloadModelAsync("qwen3:8b", CancellationToken.None);

        AssertEx.ContainsSingle(context.Server.RecordedRequests,
            request => request.Path == "/api/generate" && request.ModelName == "qwen3:8b" && request.KeepAlive == "0");
    }

    [Test]
    public async Task WarmModelAsync_WhenInvoked_PostsEmptyGenerateWithoutKeepAliveOverride()
    {
        await using var context = await CreateContextAsync("chat", "qwen3:8b");

        await context.Provider.WarmModelAsync("qwen3:8b", CancellationToken.None);

        // Warm-up must actually load weights via /api/generate — /api/show only reads metadata and never loads.
        AssertEx.False(context.Server.RecordedRequests.Any(request => request.Path == "/api/show"),
            "Warm-up must not fall back to the metadata-only /api/show request.");
        AssertEx.ContainsSingle(context.Server.RecordedRequests,
            request => request.Path == "/api/generate" && request.ModelName == "qwen3:8b");

        var generate = context.Server.RecordedRequests.Single(request => request.Path == "/api/generate");
        // Empty prompt = Ollama's documented preload shape: the model loads, nothing is generated.
        AssertEx.Equal(Convert.ToHexString(SHA256.HashData(ReadOnlySpan<byte>.Empty)), generate.PromptHash);
        // No keep_alive override: residency follows Ollama's keep_alive default (no pinning, unlike unload's "0").
        AssertEx.Null(generate.KeepAlive);
    }

    [Test]
    public async Task WarmModelAsync_WhenTokenAlreadyCancelled_ThrowsWithoutIssuingRequest()
    {
        await using var context = await CreateContextAsync("chat");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => context.Provider.WarmModelAsync("chat", cts.Token));

        AssertEx.False(context.Server.RecordedRequests.Any(request => request.Path == "/api/generate"),
            "A cancelled warm-up must not dispatch a generate request.");
    }

    [Test]
    public async Task CreateChatClient_WhenProviderMatches_ReturnsOllamaChatClientForModel()
    {
        await using var context = await CreateContextAsync("chat");

        var chatClient = context.Provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = "chat",
            ProviderName = OllamaLocalModelProvider.OllamaProviderName
        });

        var ollamaClient = AssertEx.NotNull(chatClient as OllamaApiClient);
        AssertEx.Equal("chat", ollamaClient.SelectedModel);
        AssertEx.True(chatClient is IChatClient);
    }

    [Test]
    public async Task CreateChatClient_RoutedClientUsesHardenedTransport_NormalizesConnectTimeout()
    {
        // A per-model client minted by the provider must share the hardened transport: a fired connect timeout (a
        // TaskCanceledException with no caller cancellation) has to present as HttpRequestException — the shape every
        // "Ollama unreachable" catch expects — not leak out as a raw OperationCanceledException from the default transport.
        var connectTimeout = new TaskCanceledException("connect timed out", new TimeoutException());
#pragma warning disable CA2000 // Ownership transfers to the factory, disposed at the end of the test.
        var handler = new OllamaConnectFailureHandler(new ThrowingHandler(connectTimeout));
        var httpClient = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri("http://127.0.0.1:11434") };
#pragma warning restore CA2000
        using var factory = new OllamaApiClientFactory(httpClient, ownsHttpClient: true);
        using var baseClient = factory.CreateClient(selectedModel: null);
        using var provider = new OllamaLocalModelProvider(baseClient, factory);

        var chatClient = provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = "chat",
            ProviderName = OllamaLocalModelProvider.OllamaProviderName
        });

        var thrown = await AssertEx.ThrowsAsync<HttpRequestException>(() =>
            chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: CancellationToken.None));

        AssertEx.True(ReferenceEquals(connectTimeout, thrown.InnerException), "the connect-timeout should be the inner exception");
    }

    private static async Task<ProviderTestContext> CreateContextAsync(params string[] models)
    {
        var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = models.Length > 0 ? models : ["chat"]
        }, CancellationToken.None);

        // Mirror production wiring: a single hardened transport (here a plain HttpClient pointed at the fake server) is
        // shared by the base management client and every per-model client the factory mints.
#pragma warning disable CA2000 // Ownership transfers to the factory, which the ProviderTestContext disposes.
        var httpClient = new HttpClient { BaseAddress = server.BaseAddress };
#pragma warning restore CA2000
        var factory = new OllamaApiClientFactory(httpClient, ownsHttpClient: true);
        var ollamaClient = factory.CreateClient(selectedModel: null);
        var provider = new OllamaLocalModelProvider(ollamaClient, factory);
        return new ProviderTestContext(server, ollamaClient, factory, provider);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        private readonly ICollection<T> _items;

        public CapturingProgress(ICollection<T> items)
        {
            _items = items ?? throw new ArgumentNullException(nameof(items));
        }

        public void Report(T value)
        {
            _items.Add(value);
        }
    }

    private sealed class ProviderTestContext : IAsyncDisposable
    {
        private readonly OllamaApiClientFactory _factory;

        public ProviderTestContext(FakeOllamaServer server, OllamaApiClient ollamaClient, OllamaApiClientFactory factory, OllamaLocalModelProvider provider)
        {
            Server = server;
            OllamaClient = ollamaClient;
            _factory = factory;
            Provider = provider;
        }

        public FakeOllamaServer Server { get; }

        public OllamaApiClient OllamaClient { get; }

        public OllamaLocalModelProvider Provider { get; }

        public async ValueTask DisposeAsync()
        {
            Provider.Dispose();
            OllamaClient.Dispose();
            // The factory owns the shared HttpClient; disposing the per-model/base clients above does not.
            _factory.Dispose();
            await Server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
