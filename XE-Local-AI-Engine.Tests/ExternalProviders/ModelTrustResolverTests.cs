namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The tri-state trust resolver every policy gate consults. Its whole reason to exist is the third state: an
///     <c>ext:</c> id that could not be resolved must gate exactly like a cloud model, never like a local one.
/// </summary>
/// <remarks>
///     The failure this closes is structural. An unrecognized model id falls through the cloud selection by design (the
///     orphan guard that keeps external ids routing locally), so every one of the three existing cloud checks would
///     have classified a declared-CLOUD external model as node-local — and handed it the workspace, the knowledge
///     base, custom tools and <c>run_python</c>.
/// </remarks>
public sealed class ModelTrustResolverTests
{
    [Test]
    public async Task ResolveAsync_ForADeclaredLocalExternalModel_IsLocal()
    {
        var resolver = Build(out _, ExternalProviderLocality.Local);

        AssertEx.Equal(ModelTrustLocality.Local, await resolver.ResolveAsync(ExternalProviderTestData.ModelId));
    }

    [Test]
    public async Task ResolveAsync_ForADeclaredCloudExternalModel_IsCloud()
    {
        var resolver = Build(out _, ExternalProviderLocality.Cloud);

        AssertEx.Equal(ModelTrustLocality.Cloud, await resolver.ResolveAsync(ExternalProviderTestData.ModelId));
    }

    [Test]
    public async Task ResolveAsync_ForAnUnregisteredExternalModel_IsUnresolved()
    {
        var resolver = Build(out _, ExternalProviderLocality.Local);

        AssertEx.Equal(ModelTrustLocality.Unresolved, await resolver.ResolveAsync("ext:deleted-box/qwen3"));
    }

    [Test]
    public async Task ResolveAsync_ForAMalformedExternalId_IsUnresolved()
    {
        var resolver = Build(out _, ExternalProviderLocality.Local);

        AssertEx.Equal(ModelTrustLocality.Unresolved, await resolver.ResolveAsync("ext:not-an-id"));
    }

    [Test]
    public async Task ResolveAsync_WhenTheRegistryThrows_IsUnresolvedRatherThanPropagating()
    {
        var registry = Substitute.For<IExternalProviderRegistry>();
        _ = registry.TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns<Task<ExternalProviderModelRegistration?>>(_ => throw new IOException("the store is unreadable"));
        var resolver = new ModelTrustResolver(registry,
            Substitute.For<IExternalProviderRegistryCache>(),
            Substitute.For<IActiveCloudChatClientFactory>(),
            NullLogger<ModelTrustResolver>.Instance);

        // The gates that call this have to reach a decision; a corrupt store earns "withhold", not an exception on the
        // chat path.
        AssertEx.Equal(ModelTrustLocality.Unresolved, await resolver.ResolveAsync(ExternalProviderTestData.ModelId));
    }

    [Test]
    public async Task ResolveAsync_ForANonExternalId_DelegatesToTheCloudSelection()
    {
        var resolver = Build(out var cloudFactory, ExternalProviderLocality.Local);
        _ = cloudFactory.IsCloudProviderSelected("gpt-5.6-terra").Returns(true);
        _ = cloudFactory.IsCloudProviderSelected("qwen3-27b.gguf").Returns(false);

        AssertEx.Equal(ModelTrustLocality.Cloud, await resolver.ResolveAsync("gpt-5.6-terra"));
        AssertEx.Equal(ModelTrustLocality.Local, await resolver.ResolveAsync("qwen3-27b.gguf"));
    }

    [Test]
    public async Task ResolveAsync_WithNoModel_IsLocal()
    {
        var resolver = Build(out _, ExternalProviderLocality.Local);

        // A model-less turn has no egress to gate; reporting cloud here would withhold tools from every turn that has
        // not selected a model yet.
        AssertEx.Equal(ModelTrustLocality.Local, await resolver.ResolveAsync(null));
        AssertEx.Equal(ModelTrustLocality.Local, await resolver.ResolveAsync("   "));
    }

    [Test]
    public async Task TryResolveExternalAsync_ReturnsTheDeclarationsForAnExternalIdOnly()
    {
        var resolver = Build(out _, ExternalProviderLocality.Local);

        AssertEx.Equal(ExternalProviderTestData.WireId,
            AssertEx.NotNull(await resolver.TryResolveExternalAsync(ExternalProviderTestData.ModelId)).Model.WireId);
        AssertEx.Null(await resolver.TryResolveExternalAsync("qwen3-27b.gguf"));
    }

    [Test]
    public void ClassifyExternalCached_ForANonExternalId_DeclinesToAnswer()
    {
        var resolver = Build(out _, ExternalProviderLocality.Local);

        // Null is "not my question" — the caller's own cloud flag decides. Distinct from every other answer, which is
        // a concrete locality the caller can act on without a second lookup.
        AssertEx.Null(resolver.ClassifyExternalCached("qwen3-27b.gguf"));
    }

    [Test]
    public void ClassifyExternalCached_WithAColdCache_IsUnresolved()
    {
        var registryCache = Substitute.For<IExternalProviderRegistryCache>();
        _ = registryCache.TryClassifyCached(Arg.Any<string>(), out Arg.Any<ExternalProviderModelRegistration?>()).Returns(false);
        var resolver = new ModelTrustResolver(new FakeExternalProviderRegistry(),
            registryCache,
            Substitute.For<IActiveCloudChatClientFactory>(),
            NullLogger<ModelTrustResolver>.Instance);

        // The pre-boot window. Withholding is the only safe answer, and the startup reconciliation pass primes the
        // snapshot so it is the only window in which this fires.
        AssertEx.Equal(ModelTrustLocality.Unresolved, resolver.ClassifyExternalCached(ExternalProviderTestData.ModelId));
    }

    [Test]
    public async Task ClassifyExternalCached_AfterPriming_MatchesTheAsyncAnswer()
    {
        var registry = new ExternalProviderRegistry(new FakeExternalProviderStore(
            ExternalProviderRegistryTests.Connection("local-box", ["qwen3"]),
            ExternalProviderRegistryTests.Connection("cloud-box", ["qwen3"], locality: ExternalProviderLocality.Cloud)));
        await registry.PrimeAsync();
        var resolver = new ModelTrustResolver(registry, registry, Substitute.For<IActiveCloudChatClientFactory>(), NullLogger<ModelTrustResolver>.Instance);

        AssertEx.Equal(ModelTrustLocality.Local, resolver.ClassifyExternalCached("ext:local-box/qwen3"));
        AssertEx.Equal(ModelTrustLocality.Cloud, resolver.ClassifyExternalCached("ext:cloud-box/qwen3"));
        AssertEx.Equal(ModelTrustLocality.Unresolved, resolver.ClassifyExternalCached("ext:gone/qwen3"));

        // The two paths must never disagree: one gates the send, the other gates the tools offered to that same send.
        AssertEx.Equal(await resolver.ResolveAsync("ext:local-box/qwen3"), resolver.ClassifyExternalCached("ext:local-box/qwen3")!.Value);
        AssertEx.Equal(await resolver.ResolveAsync("ext:cloud-box/qwen3"), resolver.ClassifyExternalCached("ext:cloud-box/qwen3")!.Value);
    }

    private static ModelTrustResolver Build(out IActiveCloudChatClientFactory cloudFactory, ExternalProviderLocality locality)
    {
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(locality: locality),
            ExternalProviderTestData.Model());
        cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        return new ModelTrustResolver(registry, Substitute.For<IExternalProviderRegistryCache>(), cloudFactory, NullLogger<ModelTrustResolver>.Instance);
    }
}
