namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Blocker 2: the Azure/cloud LOCALITY bit that gates node-local private data must come from the SAME routing
///     snapshot the send path routes from (<see cref="IActiveCloudChatClientFactory.IsCloudProviderSelected" />), never
///     an independent credential-store read that could classify a request local while the factory routes it to Azure
///     from its cached snapshot. A snapshot read failure FAILS CLOSED to cloud so the private-data gate withholds.
/// </summary>
public sealed class ModelCapabilityResolverTests
{
    private const string AzureDeployment = "azure-gpt-deploy";

    [Test]
    public async Task ResolveAsync_WhenRoutingSnapshotSelectsCloud_ClassifiesCloud()
    {
        var factory = Substitute.For<IActiveCloudChatClientFactory>();
        factory.IsCloudProviderSelected(AzureDeployment).Returns(true);
        var resolver = CreateResolver(factory, out _);

        var (supportsThinking, supportsTools, isCloud) = await resolver.ResolveAsync(AzureDeployment, CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(isCloud, "a deployment the factory would route to a cloud provider must classify cloud");
        AssertEx.False(supportsThinking, "Azure's declared matrix does not advertise thinking here");
        AssertEx.Equal(AzureFoundryProviderCapabilities.V0.SupportsToolCalling, supportsTools);
    }

    [Test]
    public async Task ResolveAsync_WhenRoutingSnapshotReadFails_FailsClosedToCloud()
    {
        // The credential/routing snapshot read throws (a transient store failure). The factory could still route the
        // same deployment to Azure from its cached snapshot, so classification must FAIL CLOSED to cloud — never local —
        // and keep the conservative non-thinking/non-tools default for a model it could not actually classify.
        var factory = Substitute.For<IActiveCloudChatClientFactory>();
        factory.IsCloudProviderSelected(AzureDeployment).Returns(_ => throw new InvalidOperationException("credential store unavailable"));
        var resolver = CreateResolver(factory, out _);

        var (supportsThinking, supportsTools, isCloud) = await resolver.ResolveAsync(AzureDeployment, CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(isCloud, "a snapshot read failure must fail CLOSED to cloud so the private-data gate withholds");
        AssertEx.False(supportsThinking);
        AssertEx.False(supportsTools);
    }

    [Test]
    public async Task ResolveAsync_WhenRoutingSnapshotSelectsLocal_ClassifiesLocalFromGguf()
    {
        // The factory routes this model local (no cloud selection). Locality is then the node-local runtime's — a GGUF
        // model here — so IsCloud is false and the gate does NOT withhold.
        const string localModel = "qwen3-8b.gguf";
        var factory = Substitute.For<IActiveCloudChatClientFactory>();
        factory.IsCloudProviderSelected(localModel).Returns(false);
        var resolver = CreateResolver(factory, out var providerResolver, out var ggufResolver);
        providerResolver.ResolveProviderNameForModelAsync(localModel, Arg.Any<CancellationToken>()).Returns("llama.cpp");
        ggufResolver.TryResolveAsync(localModel, Arg.Any<CancellationToken>())
                    .Returns(new GgufModelCapabilities(SupportsThinking: true, SupportsTools: true, SupportsVision: false));

        var (supportsThinking, supportsTools, isCloud) = await resolver.ResolveAsync(localModel, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(isCloud, "a locally-routed model must classify local so the gate does not withhold");
        AssertEx.True(supportsThinking);
        AssertEx.True(supportsTools);
    }

    private static ModelCapabilityResolver CreateResolver(IActiveCloudChatClientFactory factory, out ILocalModelProviderResolver providerResolver)
    {
        return CreateResolver(factory, out providerResolver, out _);
    }

    private static ModelCapabilityResolver CreateResolver(IActiveCloudChatClientFactory factory,
        out ILocalModelProviderResolver providerResolver,
        out IGgufModelCapabilityResolver ggufResolver)
    {
        providerResolver = Substitute.For<ILocalModelProviderResolver>();
        // Default: not an Ollama-routed model, so the resolver never issues an /api/show probe in these tests.
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(OllamaLocalModelProvider.OllamaProviderName + "-not");
        ggufResolver = Substitute.For<IGgufModelCapabilityResolver>();
        return new ModelCapabilityResolver(Substitute.For<IModelClassificationService>(),
            providerResolver,
            ggufResolver,
            factory,
            NullLogger<ModelCapabilityResolver>.Instance);
    }
}
