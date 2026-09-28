namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class KnowledgeModelPrewarmerTests
{
    private const string Reranker = "gpustack/bge-reranker-v2-m3-GGUF:Q4_K_M";
    private const string Embedder = "nomic-ai/nomic-embed-text-v1.5-GGUF:F16";

    private readonly ILlamaServerProcessSupervisor _supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
    private readonly ManualTimeProvider _time = new();

    [Test]
    public async Task StartWarm_RerankerAndLlamaCppEmbedderConfigured_EnsuresEachRoleOnce()
    {
        var prewarmer = CreatePrewarmer(Reranker);

        await prewarmer.StartWarm();

        await _supervisor.Received(1).EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>());
        await _supervisor.Received(1).EnsureRunningAsync(Embedder, ModelRole.Embedding, Arg.Any<CancellationToken>());
        AssertEx.Equal(2, _supervisor.ReceivedCalls().Count());
    }

    [Test]
    public async Task StartWarm_NoRerankerConfigured_WarmsOnlyTheEmbedder()
    {
        var prewarmer = CreatePrewarmer(rerankerModelName: string.Empty);

        await prewarmer.StartWarm();

        await _supervisor.Received(1).EnsureRunningAsync(Embedder, ModelRole.Embedding, Arg.Any<CancellationToken>());
        await _supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), ModelRole.Reranker, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWarm_EmbedderNotOnLlamaCpp_WarmsOnlyTheReranker()
    {
        var prewarmer = CreatePrewarmer(Reranker, embeddingProviderName: "ollama");

        await prewarmer.StartWarm();

        await _supervisor.Received(1).EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>());
        await _supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), ModelRole.Embedding, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWarm_EmbedderNotConfidentlyResolved_IsNotWarmed()
    {
        var prewarmer = CreatePrewarmer(Reranker, embedderResolved: false);

        await prewarmer.StartWarm();

        await _supervisor.Received(1).EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>());
        await _supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), ModelRole.Embedding, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequestWarm_WhileAWarmIsInFlight_CoalescesAndNeverBlocksTheCaller()
    {
        var release = new TaskCompletionSource<LlamaServerEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        _supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>()).Returns(release.Task);
        var prewarmer = CreatePrewarmer(Reranker, embeddingProviderName: "ollama");

        // The supervisor never answers until released, yet every request returns at once with the same warm.
        var first = prewarmer.StartWarm();
        prewarmer.RequestWarm();
        var second = prewarmer.StartWarm();

        AssertEx.False(first.IsCompleted);
        AssertEx.True(ReferenceEquals(first, second));
        await AssertEx.EventuallyAsync(() => _supervisor.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));

        release.SetResult(new LlamaServerEndpoint
        {
            ModelName = Reranker,
            Role = ModelRole.Reranker,
            BaseAddress = new Uri("http://127.0.0.1:1")
        });
        await first;
        await _supervisor.Received(1).EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWarm_AfterASuccessfulWarm_WarmsAgainWithoutCooldown()
    {
        var prewarmer = CreatePrewarmer(Reranker, embeddingProviderName: "ollama");

        await prewarmer.StartWarm();
        await prewarmer.StartWarm();

        await _supervisor.Received(2).EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartWarm_AfterARefusal_HonoursTheCooldownThenWarmsAgain()
    {
        _supervisor.EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>())
                   .ThrowsAsync(new LlamaRuntimeException("Not enough memory to load the reranker."));
        var prewarmer = CreatePrewarmer(Reranker, embeddingProviderName: "ollama");

        await prewarmer.StartWarm();
        _time.Advance(KnowledgeModelPrewarmer.RetryCooldown - TimeSpan.FromSeconds(1));
        await prewarmer.StartWarm();
        await _supervisor.Received(1).EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromSeconds(1));
        await prewarmer.StartWarm();
        await _supervisor.Received(2).EnsureRunningAsync(Reranker, ModelRole.Reranker, Arg.Any<CancellationToken>());
    }

    private KnowledgeModelPrewarmer CreatePrewarmer(string rerankerModelName, string embeddingProviderName = LlamaServerProviderConstants.ProviderName, bool embedderResolved = true)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(embeddingProviderName).Returns(provider);

        var embeddingModelResolver = Substitute.For<IEmbeddingModelResolver>();
        embeddingModelResolver.ResolveAsync(provider, Arg.Any<CancellationToken>())
                              .Returns(new EmbeddingModelResolution
                              {
                                  Name = embedderResolved ? Embedder : "nomic-embed-text",
                                  IsConfident = embedderResolved
                              });

        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);

        return new KnowledgeModelPrewarmer(_supervisor,
            providerResolver,
            embeddingModelResolver,
            Options.Create(new KnowledgeBaseOptions
            {
                RerankerModelName = rerankerModelName,
                EmbeddingProviderName = embeddingProviderName
            }),
            lifetime,
            _time,
            NullLogger<KnowledgeModelPrewarmer>.Instance);
    }
}
