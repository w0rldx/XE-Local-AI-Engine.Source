namespace XE_Local_AI_Engine.Tests.Capacity;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Verifies that capacity projects the exact shared process allocation instead of recomputing fit math.</summary>
public sealed class ModelFootprintProviderTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Model = "bartowski/Some-Model-GGUF:Q6_K";

    [Test]
    public async Task Footprint_ProjectsSharedAllocationResources()
    {
        var expected = new ResourceFootprint(6 * Gb, 3 * Gb);
        var (provider, allocationResolver) = BuildProvider(new ProcessContextAllocation(
            ProcessContextTokens: 16384,
            ModelTrainContextTokens: 32768,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.Hybrid,
            expected,
            ContentIdentity: "sha256",
            CacheKey: "cache"));

        var footprint = await provider.ResolveFootprintAsync(Model, ModelRole.Chat, GpuProfile(), CancellationToken.None);

        AssertEx.True(footprint.IsKnown);
        AssertEx.Equal(expected, footprint.Resources);
        await allocationResolver.Received(1).ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            Arg.Is<ResolvedLaunchArguments>(static args => args.ExploreMode),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Footprint_WhenAllocationUnknown_ReturnsUnknown()
    {
        var (provider, _) = BuildProvider(allocation: null);

        var footprint = await provider.ResolveFootprintAsync(Model, ModelRole.Chat, GpuProfile(), CancellationToken.None);

        AssertEx.False(footprint.IsKnown);
        AssertEx.Equal(ResourceFootprint.Zero, footprint.Resources);
    }

    [Test]
    public async Task Footprint_PreservesRoleAndFrozenArguments()
    {
        var frozen = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 42);
        var expected = new ProcessContextAllocation(8192,
            32768,
            ProcessContextAllocationSource.FrozenProfile,
            ProcessPlacementMode.GpuResident,
            new ResourceFootprint(4 * Gb, 2 * Gb),
            "sha256",
            "cache");
        var (provider, allocationResolver) = BuildProvider(expected, frozen, GpuVariant.Vulkan);

        await provider.ResolveFootprintAsync(Model, ModelRole.Reranker, GpuProfile(), CancellationToken.None);

        await allocationResolver.Received(1).ResolveAsync(Model,
            ModelRole.Reranker,
            GpuVariant.Vulkan,
            frozen,
            Arg.Any<CancellationToken>());
    }

    private static (ModelFootprintProvider Provider, IProcessContextAllocationResolver AllocationResolver) BuildProvider(
        ProcessContextAllocation? allocation,
        ResolvedLaunchArguments? resolved = null,
        GpuVariant variant = GpuVariant.Cuda)
    {
        var variantSelector = Substitute.For<IGpuVariantSelector>();
        variantSelector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(variant));

        var profileResolver = Substitute.For<IInferenceProfileResolver>();
        profileResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult(resolved ?? ResolvedLaunchArguments.Explore()));

        var allocationResolver = Substitute.For<IProcessContextAllocationResolver>();
        allocationResolver.ResolveAsync(Arg.Any<string>(),
                              Arg.Any<ModelRole>(),
                              Arg.Any<GpuVariant>(),
                              Arg.Any<ResolvedLaunchArguments>(),
                              Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(allocation));

        return (new ModelFootprintProvider(variantSelector, profileResolver, allocationResolver), allocationResolver);
    }

    private static HardwareProfile GpuProfile()
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = 32 * Gb,
            AvailableVramBytes = 30 * Gb,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }
}
