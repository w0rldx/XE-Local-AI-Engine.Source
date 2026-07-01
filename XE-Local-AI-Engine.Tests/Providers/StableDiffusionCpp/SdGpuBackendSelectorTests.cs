namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Configuration;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SdGpuBackendSelectorTests
{
    [Test]
    [Arguments(GpuVendor.Nvidia, true, SdGpuBackend.Cuda)]
    [Arguments(GpuVendor.Nvidia, false, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Amd, true, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Amd, false, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Intel, false, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.None, true, SdGpuBackend.Cpu)]
    [Arguments(GpuVendor.Unknown, false, SdGpuBackend.Cpu)]
    public void SelectForVendor_AppliesOsAwareRule(GpuVendor vendor, bool isWindows, SdGpuBackend expected)
    {
        AssertEx.Equal(expected, SdGpuBackendSelector.SelectForVendor(vendor, isWindows));
    }

    [Test]
    public async Task SelectBackendAsync_LinuxNvidia_FallsBackToVulkan()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(GpuVendor.Nvidia));
        var selector = new SdGpuBackendSelector(profiler, isWindows: false);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Vulkan, backend);
    }

    [Test]
    public async Task SelectBackendAsync_ActiveOverride_ShortCircuitsProbe()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        var overrideOptions = new StableDiffusionServerRuntimeOverrideOptions
        {
            ServerPath = "/opt/sd-server",
            Backend = SdGpuBackend.Cuda
        };
        var selector = new SdGpuBackendSelector(profiler, isWindows: false, overrideOptions);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Cuda, backend);
        await profiler.DidNotReceive().GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static HardwareProfile Profile(GpuVendor vendor)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 32L * 1024 * 1024 * 1024,
            AvailableRamBytes = 16L * 1024 * 1024 * 1024,
            VramBytes = 16L * 1024 * 1024 * 1024,
            VramKnown = true,
            GpuVendor = vendor,
            GpuAccelAvailable = vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel,
            CpuCores = 16,
            FreeDiskBytes = 512L * 1024 * 1024 * 1024
        };
    }
}
