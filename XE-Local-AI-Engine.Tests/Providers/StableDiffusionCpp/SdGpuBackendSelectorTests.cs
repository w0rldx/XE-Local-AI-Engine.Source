namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Configuration;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SdGpuBackendSelectorTests
{
    [Test]
    // Windows NVIDIA → CUDA regardless of the Vulkan device probe (Windows path unchanged).
    [Arguments(GpuVendor.Nvidia, true, false, SdGpuBackend.Cuda)]
    [Arguments(GpuVendor.Nvidia, true, true, SdGpuBackend.Cuda)]
    // Linux NVIDIA → Vulkan only when a Vulkan device is confirmed; else CPU (the WSL fail-safe).
    [Arguments(GpuVendor.Nvidia, false, true, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Nvidia, false, false, SdGpuBackend.Cpu)]
    // Windows AMD/Intel → Vulkan unconditionally (Windows path unchanged).
    [Arguments(GpuVendor.Amd, true, false, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Intel, true, false, SdGpuBackend.Vulkan)]
    // Linux AMD/Intel → Vulkan only when a Vulkan device is confirmed; else CPU.
    [Arguments(GpuVendor.Amd, false, true, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Amd, false, false, SdGpuBackend.Cpu)]
    [Arguments(GpuVendor.Intel, false, false, SdGpuBackend.Cpu)]
    // No/unknown GPU → CPU everywhere.
    [Arguments(GpuVendor.None, true, false, SdGpuBackend.Cpu)]
    [Arguments(GpuVendor.Unknown, false, true, SdGpuBackend.Cpu)]
    public void SelectForVendor_AppliesOsAndVulkanDeviceAwareRule(GpuVendor vendor, bool isWindows, bool vulkanDeviceAvailable, SdGpuBackend expected)
    {
        AssertEx.Equal(expected, SdGpuBackendSelector.SelectForVendor(vendor, isWindows, vulkanDeviceAvailable));
    }

    [Test]
    public async Task SelectBackendAsync_LinuxNvidia_NoVulkanDevice_FallsBackToCpu()
    {
        // The WSL2 gap: an NVIDIA GPU is present but no Vulkan device enumerates, so Vulkan would hard-fail → CPU.
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(GpuVendor.Nvidia));
        var selector = new SdGpuBackendSelector(profiler, isWindows: false, new FakeVulkanDeviceProbe(hasDevice: false));

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Cpu, backend);
    }

    [Test]
    public async Task SelectBackendAsync_LinuxNvidia_WithVulkanDevice_SelectsVulkan()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(GpuVendor.Nvidia));
        var selector = new SdGpuBackendSelector(profiler, isWindows: false, new FakeVulkanDeviceProbe(hasDevice: true));

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Vulkan, backend);
    }

    [Test]
    public async Task SelectBackendAsync_WindowsNvidia_SelectsCuda_WithoutConsultingProbe()
    {
        // Windows path unchanged: NVIDIA → CUDA and the Vulkan device probe is never consulted.
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(GpuVendor.Nvidia));
        var probe = new ThrowingVulkanDeviceProbe();
        var selector = new SdGpuBackendSelector(profiler, isWindows: true, probe);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Cuda, backend);
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
        var selector = new SdGpuBackendSelector(profiler, isWindows: false, new ThrowingVulkanDeviceProbe(), overrideOptions);

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

    private sealed class FakeVulkanDeviceProbe(bool hasDevice) : IVulkanDeviceProbe
    {
        public bool HasEnumerableVulkanDevice()
        {
            return hasDevice;
        }
    }

    private sealed class ThrowingVulkanDeviceProbe : IVulkanDeviceProbe
    {
        public bool HasEnumerableVulkanDevice()
        {
            throw new InvalidOperationException("The Vulkan device probe must not be consulted on this path.");
        }
    }
}
