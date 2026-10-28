namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Freeze stamps the launch-identity scheme of the intended identity once and never recomputes it, so a later
///     build tells a comparable hash from one computed by a scheme it no longer runs without re-projecting the
///     executing box's conditions.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkPhaseLaunchResolverTests
{
    [Test]
    public async Task ResolveAsync_StampsTheCurrentIdentityScheme()
    {
        var frozen = await Resolver().ResolveAsync("model.gguf",
            requiredContextTokens: 4096,
            requestedKvCacheType: null,
            capabilities: null,
            GpuVariant.Cpu,
            CancellationToken.None);

        AssertEx.Equal<int?>(LlamaServerLaunchProjection.IdentitySchemeVersion, frozen.Intent.LaunchIdentityScheme);
        AssertEx.NotNullOrEmpty(frozen.Intent.IntendedLaunchIdentity);
    }

    private static BenchmarkPhaseLaunchResolver Resolver()
    {
        var profiles = Substitute.For<IInferenceProfileResolver>();
        profiles.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                .Returns(ResolvedLaunchArguments.Replay(ctxSize: 8192));
        var variants = Substitute.For<IGpuVariantSelector>();
        variants.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(GpuVariant.Cpu);
        var options = new LlamaServerLaunchPolicyOptions();
        return new BenchmarkPhaseLaunchResolver(profiles,
            variants,
            Substitute.For<ILlamaServerLaunchCapabilityInspector>(),
            new FakeLaunchFallbackStore(),
            new LlamaServerLaunchPolicy(options, new FakeLaunchFallbackStore(), NullLogger<LlamaServerLaunchPolicy>.Instance),
            options);
    }
}
