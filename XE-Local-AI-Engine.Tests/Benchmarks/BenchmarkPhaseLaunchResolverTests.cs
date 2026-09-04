namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Freeze stamps the launch-identity scheme it computed the intended identity under, once, and never recomputes it.
///     That stamp is what lets a later build tell "this hash is comparable to mine" from "this hash was computed by a
///     scheme I no longer run" without re-projecting anything from the executing box's conditions.
/// </summary>
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
