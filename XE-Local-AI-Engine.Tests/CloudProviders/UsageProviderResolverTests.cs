namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the usage-provider attribution: the pure <see cref="UsageProviderClassifier" />
///     mapping over all five outcomes, and the <see cref="UsageProviderResolver" /> orchestration — cloud-first precedence,
///     local fallback, and the never-throw / null-model / failed-lookup degradations to <c>unknown</c>.
/// </summary>
public sealed class UsageProviderResolverTests
{
    [Test]
    public void Classify_CloudCodex_MapsToCodex()
    {
        AssertEx.Equal(AgentUsageProviders.Codex, UsageProviderClassifier.Classify("codex", localProviderName: null));
    }

    [Test]
    public void Classify_CloudAzure_MapsToAzure()
    {
        AssertEx.Equal(AgentUsageProviders.Azure, UsageProviderClassifier.Classify("azure", localProviderName: null));
    }

    [Test]
    public void Classify_LocalLlamaServer_MapsToLocal()
    {
        AssertEx.Equal(AgentUsageProviders.Local, UsageProviderClassifier.Classify(cloudProviderName: null, LlamaServerProviderConstants.ProviderName));
    }

    [Test]
    public void Classify_LocalOllama_MapsToOllama()
    {
        AssertEx.Equal(AgentUsageProviders.Ollama, UsageProviderClassifier.Classify(cloudProviderName: null, OllamaLocalModelProvider.OllamaProviderName));
    }

    [Test]
    public void Classify_UnrecognizedNames_MapToUnknown()
    {
        AssertEx.Equal(AgentUsageProviders.Unknown, UsageProviderClassifier.Classify(cloudProviderName: null, localProviderName: null));
        AssertEx.Equal(AgentUsageProviders.Unknown, UsageProviderClassifier.Classify("some-future-cloud", localProviderName: null));
        AssertEx.Equal(AgentUsageProviders.Unknown, UsageProviderClassifier.Classify(cloudProviderName: null, "vllm"));
    }

    [Test]
    public async Task ResolveAsync_WhenModelBlank_ReturnsUnknownWithoutConsultingCloudFactory()
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        var resolver = Create(cloudFactory, providerResolver);

        AssertEx.Equal(AgentUsageProviders.Unknown, await resolver.ResolveAsync("   "));
        AssertEx.Equal(AgentUsageProviders.Unknown, await resolver.ResolveAsync(modelName: null));

        // A model-less turn must not be mislabelled as the signed-in cloud provider — the factory is never consulted.
        _ = cloudFactory.DidNotReceive().ResolveActiveCloudProviderName(Arg.Any<string?>());
    }

    [Test]
    public async Task ResolveAsync_WhenCloudSelected_AttributesCloudWithoutLocalLookup()
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.ResolveActiveCloudProviderName("gpt-5-codex").Returns("codex");
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        var resolver = Create(cloudFactory, providerResolver);

        AssertEx.Equal(AgentUsageProviders.Codex, await resolver.ResolveAsync("gpt-5-codex"));

        // Cloud precedence: the local resolver is not consulted once a cloud provider is selected.
        _ = providerResolver.DidNotReceive().ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_WhenNoCloud_ResolvesLocalRuntime()
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.ResolveActiveCloudProviderName(Arg.Any<string?>()).Returns((string?)null);
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync("qwen3:8b", Arg.Any<CancellationToken>()).Returns(OllamaLocalModelProvider.OllamaProviderName);
        var resolver = Create(cloudFactory, providerResolver);

        AssertEx.Equal(AgentUsageProviders.Ollama, await resolver.ResolveAsync("qwen3:8b"));
    }

    [Test]
    public async Task ResolveAsync_WhenLocalResolverThrows_DegradesToUnknown()
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.ResolveActiveCloudProviderName(Arg.Any<string?>()).Returns((string?)null);
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns<string>(_ => throw new InvalidOperationException("resolver down"));
        var resolver = Create(cloudFactory, providerResolver);

        // Attribution must never throw out of terminalization — a failed lookup is recorded as unknown.
        AssertEx.Equal(AgentUsageProviders.Unknown, await resolver.ResolveAsync("some-model"));
    }

    [Test]
    public async Task ResolveAsync_WhenCloudFactoryThrows_FallsBackToLocalLookup()
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.ResolveActiveCloudProviderName(Arg.Any<string?>()).Returns(_ => throw new InvalidOperationException("snapshot read failed"));
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync("local-gguf", Arg.Any<CancellationToken>()).Returns(LlamaServerProviderConstants.ProviderName);
        var resolver = Create(cloudFactory, providerResolver);

        AssertEx.Equal(AgentUsageProviders.Local, await resolver.ResolveAsync("local-gguf"));
    }

    private static UsageProviderResolver Create(IActiveCloudChatClientFactory cloudFactory, ILocalModelProviderResolver providerResolver)
    {
        return new UsageProviderResolver(cloudFactory, providerResolver, TimeProvider.System, NullLogger<UsageProviderResolver>.Instance);
    }
}
