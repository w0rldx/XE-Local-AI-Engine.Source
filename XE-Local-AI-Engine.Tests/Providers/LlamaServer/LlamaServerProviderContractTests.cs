namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.AI;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the provider satisfies all 8 <c>ILocalModelProvider</c> members over a faked supervisor + a fixed GGUF
///     store with no network. Asserts the provider-name guard, the GGUF-store mapping for list/pull/delete, the
///     supervisor mapping for warm/unload/health, and that the deferred chat/embedding clients ensure-run the right
///     <c>(model, role)</c> on first use and route to the supervisor's endpoint.
/// </summary>
public sealed class LlamaServerProviderContractTests
{
    private const string Model = "qwen2.5-coder";
    private const string Provider = "llamacpp";

    [Test]
    public void ProviderName_IsLlamacpp()
    {
        var provider = CreateProvider(Substitute.For<ILlamaServerProcessSupervisor>());
        AssertEx.Equal("llamacpp", provider.ProviderName);
    }

    [Test]
    public void CreateChatClient_ProviderNameMismatch_Throws()
    {
        var provider = CreateProvider(Substitute.For<ILlamaServerProcessSupervisor>());

        var ex = Assert.Throws<ArgumentException>(() =>
            provider.CreateChatClient(new LocalModelSelection
            {
                ModelName = Model,
                ProviderName = "ollama"
            }));

        AssertEx.Contains(ex!.Message, "does not match", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public void CreateEmbeddingGenerator_ProviderNameMismatch_Throws()
    {
        var provider = CreateProvider(Substitute.For<ILlamaServerProcessSupervisor>());

        Assert.Throws<ArgumentException>(() =>
            provider.CreateEmbeddingGenerator(new LocalModelSelection
            {
                ModelName = Model,
                ProviderName = "ollama"
            }));
    }

    [Test]
    public async Task ListModelsAsync_DelegatesToGgufStore()
    {
        var store = new FakeModelStore("/fake/models/m.gguf", [Model, "other"]);
        var provider = new LlamaServerLocalModelProvider(Substitute.For<ILlamaServerProcessSupervisor>(), store);

        var models = await provider.ListModelsAsync(CancellationToken.None);

        AssertEx.Equal(expected: 2, models.Count);
        AssertEx.Contains(models, m => m.ModelName == Model && m.ProviderName == Provider);
    }

    [Test]
    public async Task PullModelAsync_DelegatesToStore_ParsingModelNameIntoRequest()
    {
        var store = Substitute.For<IGgufModelStore>();
        store.EnsureModelAsync(Arg.Any<GgufModelRequest>(), Arg.Any<IProgress<PullProgress>>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(new GgufModelHandle(Model, "/fake/m.gguf", "Q4_K_M", SizeBytes: 1, Sha256: null, "rev", GgufRole.Unknown)));
        var provider = new LlamaServerLocalModelProvider(Substitute.For<ILlamaServerProcessSupervisor>(), store);

        await provider.PullModelAsync($"{Model}:Q4_K_M", progress: null, CancellationToken.None);

        await store.Received(1).EnsureModelAsync(Arg.Is<GgufModelRequest>(r => r.RepoId == Model && r.Quant == "Q4_K_M"),
            Arg.Any<IProgress<PullProgress>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteModelAsync_DelegatesToStore()
    {
        var store = Substitute.For<IGgufModelStore>();
        var provider = new LlamaServerLocalModelProvider(Substitute.For<ILlamaServerProcessSupervisor>(), store);

        await provider.DeleteModelAsync(Model, CancellationToken.None);

        await store.Received(1).DeleteModelAsync(Model, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WarmModelAsync_EnsuresRunningChatProcess()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Model, ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns(new LlamaServerEndpoint(Model, ModelRole.Chat, new Uri("http://127.0.0.1:18100/v1")));
        var provider = CreateProvider(supervisor);

        await provider.WarmModelAsync(Model, CancellationToken.None);

        await supervisor.Received(1).EnsureRunningAsync(Model, ModelRole.Chat, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnloadModelAsync_EvictsBothRoles()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var provider = CreateProvider(supervisor);

        await provider.UnloadModelAsync(Model, CancellationToken.None);

        await supervisor.Received(1).EvictAsync(Model, ModelRole.Chat, Arg.Any<CancellationToken>());
        await supervisor.Received(1).EvictAsync(Model, ModelRole.Embedding, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckHealthAsync_AggregatesSupervisorHealth_HealthyWhenOperational()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns([new LlamaServerProcessHealth(Model, ModelRole.Chat, IsResponsive: true, "ok")]);
        var provider = CreateProvider(supervisor);

        var health = await provider.CheckHealthAsync(CancellationToken.None);

        AssertEx.True(health.IsHealthy);
        AssertEx.Equal(Provider, health.ProviderName);
        AssertEx.Contains(health.Diagnostics, d => d.Contains(Model, StringComparison.Ordinal));
    }

    [Test]
    public async Task CheckHealthAsync_SupervisorThrows_ReportsUnhealthy()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.CheckHealthAsync(Arg.Any<CancellationToken>())
                  .Returns<IReadOnlyList<LlamaServerProcessHealth>>(_ => throw new InvalidOperationException("boom"));
        var provider = CreateProvider(supervisor);

        var health = await provider.CheckHealthAsync(CancellationToken.None);

        AssertEx.False(health.IsHealthy);
    }

    [Test]
    public async Task CreateChatClient_FirstUse_EnsuresRunningChatRole_ForSelectedModel()
    {
        // Make ensure-running observable without any network: throw a sentinel after recording the call so the
        // deferred wrapper never reaches the real OpenAI adapter.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Model, ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns<LlamaServerEndpoint>(_ => throw new TimeoutException("sentinel"));
        var provider = CreateProvider(supervisor);

        var client = provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = Model,
            ProviderName = Provider
        });

        await AssertEx.ThrowsAsync<TimeoutException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        // Ensure-running was triggered exactly once for the chat role + selected model (not eagerly in the factory).
        await supervisor.Received(1).EnsureRunningAsync(Model, ModelRole.Chat, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateEmbeddingGenerator_FirstUse_EnsuresRunningEmbeddingRole_ForSelectedModel()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Model, ModelRole.Embedding, Arg.Any<CancellationToken>())
                  .Returns<LlamaServerEndpoint>(_ => throw new TimeoutException("sentinel"));
        var provider = CreateProvider(supervisor);

        var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = Model,
            ProviderName = Provider
        });

        // The embedding wrapper re-shapes LlamaRuntimeException to IOException for the lexical fallback; a generic
        // sentinel flows through unwrapped, proving ensure-running runs on first GenerateAsync (not eagerly).
        await AssertEx.ThrowsAsync<TimeoutException>(() =>
            generator.GenerateAsync(["text"], options: null, CancellationToken.None));

        await supervisor.Received(1).EnsureRunningAsync(Model, ModelRole.Embedding, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateEmbeddingGenerator_ProcessUnavailable_WrapsToIOException_ForLexicalFallback()
    {
        // EmbeddingPlaybookRetrievalRanker degrades to lexical only on HttpRequestException/IOException. The
        // supervisor surfaces process-unavailability as LlamaRuntimeException → must be re-shaped to IOException.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Model, ModelRole.Embedding, Arg.Any<CancellationToken>())
                  .Returns<LlamaServerEndpoint>(_ => throw new LlamaRuntimeException("no embedding process available"));
        var provider = CreateProvider(supervisor);

        var generator = provider.CreateEmbeddingGenerator(new LocalModelSelection
        {
            ModelName = Model,
            ProviderName = Provider
        });

        await AssertEx.ThrowsAsync<IOException>(() =>
            generator.GenerateAsync(["text"], options: null, CancellationToken.None));
    }

    private static LlamaServerLocalModelProvider CreateProvider(ILlamaServerProcessSupervisor supervisor)
    {
        return new LlamaServerLocalModelProvider(supervisor, new FakeModelStore("/fake/models/m.gguf", [Model]));
    }
}
