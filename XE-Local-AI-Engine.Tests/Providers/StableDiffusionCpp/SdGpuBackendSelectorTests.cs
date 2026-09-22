namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class SdGpuBackendSelectorTests
{
    [Test]
    // Windows NVIDIA → CUDA only when a CUDA device enumerates; without one, the Windows Vulkan mapping.
    [Arguments(GpuVendor.Nvidia, true, false, true, SdGpuBackend.Cuda)]
    [Arguments(GpuVendor.Nvidia, true, true, true, SdGpuBackend.Cuda)]
    [Arguments(GpuVendor.Nvidia, true, false, false, SdGpuBackend.Vulkan)]
    // Linux NVIDIA → Vulkan only when a Vulkan device is confirmed; else CPU (the WSL fail-safe). CUDA is never a Linux prebuilt.
    [Arguments(GpuVendor.Nvidia, false, true, true, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Nvidia, false, false, true, SdGpuBackend.Cpu)]
    // Windows AMD/Intel → Vulkan unconditionally (Windows path unchanged).
    [Arguments(GpuVendor.Amd, true, false, true, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Intel, true, false, true, SdGpuBackend.Vulkan)]
    // Linux AMD/Intel → Vulkan only when a Vulkan device is confirmed; else CPU.
    [Arguments(GpuVendor.Amd, false, true, true, SdGpuBackend.Vulkan)]
    [Arguments(GpuVendor.Amd, false, false, true, SdGpuBackend.Cpu)]
    [Arguments(GpuVendor.Intel, false, false, true, SdGpuBackend.Cpu)]
    // No/unknown GPU → CPU everywhere.
    [Arguments(GpuVendor.None, true, false, true, SdGpuBackend.Cpu)]
    [Arguments(GpuVendor.Unknown, false, true, true, SdGpuBackend.Cpu)]
    public void SelectForVendor_AppliesOsAndDeviceAwareRule(GpuVendor vendor,
        bool isWindows,
        bool vulkanDeviceAvailable,
        bool cudaDeviceAvailable,
        SdGpuBackend expected)
    {
        AssertEx.Equal(expected, SdGpuBackendSelector.SelectForVendor(vendor, isWindows, vulkanDeviceAvailable, cudaDeviceAvailable));
    }

    [Test]
    public async Task SelectBackendAsync_LinuxNvidia_NoVulkanDevice_FallsBackToCpu()
    {
        // The WSL2 gap: an NVIDIA GPU is present but no Vulkan device enumerates, so Vulkan would hard-fail → CPU.
        var selector = new SdGpuBackendSelector(Profiler(GpuVendor.Nvidia), isWindows: false, new FakeVulkanDeviceProbe(hasDevice: false));

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Cpu, backend);
    }

    [Test]
    public async Task SelectBackendAsync_LinuxNvidia_WithVulkanDevice_SelectsVulkan()
    {
        var selector = new SdGpuBackendSelector(Profiler(GpuVendor.Nvidia), isWindows: false, new FakeVulkanDeviceProbe(hasDevice: true));

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Vulkan, backend);
    }

    [Test]
    public async Task SelectBackendAsync_WindowsNvidia_ConsultsCudaProbe_AndSelectsCudaWhenDevicePresent()
    {
        // The Windows branch is no longer blind: it asks the CUDA probe before committing to the CUDA prebuilt.
        var probe = new FakeCudaDeviceProbe(hasDevice: true);
        var selector = new SdGpuBackendSelector(Profiler(GpuVendor.Nvidia), isWindows: true, new ThrowingVulkanDeviceProbe(), probe);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Cuda, backend);
        AssertEx.Equal(expected: 1, probe.Calls);
    }

    [Test]
    public async Task SelectBackendAsync_WindowsNvidia_ZeroCudaDevices_FallsBackToVulkan_AndWarns()
    {
        // The tester box: nvidia-smi reports an NVIDIA GPU, no CUDA device enumerates, and sd-server exited on every
        // spawn. Vulkan is served instead, and the reason is stated once at Warning.
        var logger = new RecordingLogger<SdGpuBackendSelector>();
        var selector = new SdGpuBackendSelector(Profiler(GpuVendor.Nvidia),
            isWindows: true,
            new ThrowingVulkanDeviceProbe(),
            new FakeCudaDeviceProbe(hasDevice: false),
            logger: logger);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Vulkan, backend);
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "no CUDA device could be enumerated"));
    }

    [Test]
    public async Task SelectBackendAsync_WindowsAmd_DoesNotConsultCudaProbe()
    {
        // The CUDA probe is consulted only where it can change the decision.
        var probe = new FakeCudaDeviceProbe(hasDevice: false);
        var selector = new SdGpuBackendSelector(Profiler(GpuVendor.Amd), isWindows: true, new ThrowingVulkanDeviceProbe(), probe);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Vulkan, backend);
        AssertEx.Equal(expected: 0, probe.Calls);
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
        var selector = new SdGpuBackendSelector(profiler,
            isWindows: false,
            new ThrowingVulkanDeviceProbe(),
            overrideOptions: overrideOptions);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Cuda, backend);
        await profiler.DidNotReceive().GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(true, true, true)]
    [Arguments(true, false, false)]
    // Non-Windows never inspects the filesystem: the OS rule already rules CUDA out, so the verdict is "unknown" = present.
    [Arguments(false, false, true)]
    public void DefaultCudaDeviceProbe_ReportsAbsentOnlyWhenTheWindowsDriverLibraryIsMissing(bool isWindows, bool driverLibraryPresent, bool expected)
    {
        var probe = new DefaultCudaDeviceProbe(isWindows, () => driverLibraryPresent);

        AssertEx.Equal(expected, probe.HasEnumerableCudaDevice());
    }

    [Test]
    public void DefaultCudaDeviceProbe_FilesystemFailure_ReadsAsPresent()
    {
        // A false "absent" would strand a healthy NVIDIA box on Vulkan, so an unreadable system directory reads as present.
        var probe = new DefaultCudaDeviceProbe(isWindows: true, () => throw new UnauthorizedAccessException("denied"));

        AssertEx.True(probe.HasEnumerableCudaDevice());
    }

    private static IHardwareProfiler Profiler(GpuVendor vendor)
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(vendor));
        return profiler;
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

    private sealed class FakeVulkanDeviceProbe : IVulkanDeviceProbe
    {
        private readonly bool _hasDevice;

        public FakeVulkanDeviceProbe(bool hasDevice)
        {
            _hasDevice = hasDevice;
        }

        public bool HasEnumerableVulkanDevice()
        {
            return _hasDevice;
        }
    }

    private sealed class ThrowingVulkanDeviceProbe : IVulkanDeviceProbe
    {
        public bool HasEnumerableVulkanDevice()
        {
            throw new InvalidOperationException("The Vulkan device probe must not be consulted on this path.");
        }
    }

    /// <summary>Counts its consultations so a test can assert the probe was (or was not) reached.</summary>
    private sealed class FakeCudaDeviceProbe : ICudaDeviceProbe
    {
        private readonly bool _hasDevice;
        private int _calls;

        public FakeCudaDeviceProbe(bool hasDevice)
        {
            _hasDevice = hasDevice;
        }

        public int Calls => Volatile.Read(ref _calls);

        public bool HasEnumerableCudaDevice()
        {
            Interlocked.Increment(ref _calls);
            return _hasDevice;
        }
    }
}
