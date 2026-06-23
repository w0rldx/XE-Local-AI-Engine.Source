namespace XE_Local_AI_Engine.Tests.Providers.Ollama;

using Microsoft.Extensions.AI;
using OllamaSharp;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama;
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
            request => request.Path == "/api/generate" && request.ModelName == "qwen3:8b");
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

    private static async Task<ProviderTestContext> CreateContextAsync(params string[] models)
    {
        var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
        {
            Models = models.Length > 0 ? models : ["chat"]
        }, CancellationToken.None);

        var ollamaClient = new OllamaApiClient(server.BaseAddress);
        var provider = new OllamaLocalModelProvider(ollamaClient);
        return new ProviderTestContext(server, ollamaClient, provider);
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
        public ProviderTestContext(FakeOllamaServer server, OllamaApiClient ollamaClient, OllamaLocalModelProvider provider)
        {
            Server = server;
            OllamaClient = ollamaClient;
            Provider = provider;
        }

        public FakeOllamaServer Server { get; }

        public OllamaApiClient OllamaClient { get; }

        public OllamaLocalModelProvider Provider { get; }

        public async ValueTask DisposeAsync()
        {
            Provider.Dispose();
            OllamaClient.Dispose();
            await Server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
