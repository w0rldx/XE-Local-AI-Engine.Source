namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Brief test 1, selection half. The rule is short because upstream's asset matrix is short, and the case that
///     matters most is the one a reader gets wrong: NVIDIA on LINUX selects CPU, because whisper.cpp publishes no Linux
///     CUDA prebuilt and selecting CUDA would resolve bytes that do not exist.
/// </summary>
public sealed class WhisperBackendSelectorTests
{
    [Test]
    public void SelectForVendor_WindowsNvidia_ReturnsCuda()
    {
        AssertEx.Equal(WhisperBackend.Cuda, WhisperBackendSelector.SelectForVendor(GpuVendor.Nvidia, isWindows: true));
    }

    [Test]
    public void SelectForVendor_LinuxNvidia_ReturnsCpu()
    {
        // The CUDA lane on Linux is the managed source build or the bring-your-own override, both of which
        // short-circuit before this rule is consulted at all.
        AssertEx.Equal(WhisperBackend.Cpu, WhisperBackendSelector.SelectForVendor(GpuVendor.Nvidia, isWindows: false));
    }

    [Test]
    [Arguments(GpuVendor.Amd, true)]
    [Arguments(GpuVendor.Amd, false)]
    [Arguments(GpuVendor.Intel, true)]
    [Arguments(GpuVendor.Intel, false)]
    [Arguments(GpuVendor.None, true)]
    [Arguments(GpuVendor.Unknown, false)]
    public void SelectForVendor_AmdOrIntel_ReturnsCpu(GpuVendor vendor, bool isWindows)
    {
        // V1 pins no Vulkan/Metal/OpenCL whisper backend, so a non-NVIDIA GPU has no accelerated asset to resolve.
        AssertEx.Equal(WhisperBackend.Cpu, WhisperBackendSelector.SelectForVendor(vendor, isWindows));
    }

    [Test]
    public async Task SelectBackend_OverrideActive_ShortCircuitsTheProbe()
    {
        // The live host may report a different or absent GPU than the machine the operator's binary was built on, so
        // the probe must not be consulted at all.
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns<Task<HardwareProfile>>(_ => throw new InvalidOperationException("An active override must not probe hardware."));
        var selector = new WhisperBackendSelector(profiler,
            isWindows: false,
            new WhisperServerRuntimeOverrideOptions
            {
                ServerPath = "/opt/whisper/whisper-server",
                Backend = WhisperBackend.Cuda
            });

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(WhisperBackend.Cuda, backend);
    }

    [Test]
    public async Task SelectBackend_ManagedSignalActive_WinsOverTheProbe()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(GpuVendor.Nvidia));
        var signal = new WhisperManagedSourceBuildSignal();
        signal.SetActive(WhisperBackend.Cuda);
        var selector = new WhisperBackendSelector(profiler, isWindows: false, overrideOptions: null, signal);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(WhisperBackend.Cuda, backend,
            "A validated managed Linux CUDA build must stay selected despite the missing prebuilt.");
    }

    [Test]
    public async Task SelectBackend_ManagedSignalCleared_FallsBackToTheVendorRule()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(GpuVendor.Nvidia));
        var signal = new WhisperManagedSourceBuildSignal();
        signal.SetActive(WhisperBackend.Cuda);
        signal.Clear();
        var selector = new WhisperBackendSelector(profiler, isWindows: false, overrideOptions: null, signal);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(WhisperBackend.Cpu, backend,
            "A tombstoned managed runtime must stop steering selection toward a build that no longer works.");
    }

    private static HardwareProfile Profile(GpuVendor vendor) =>
        new()
        {
            TotalRamBytes = 32L * 1024 * 1024 * 1024,
            AvailableRamBytes = 16L * 1024 * 1024 * 1024,
            VramBytes = 32L * 1024 * 1024 * 1024,
            AvailableVramBytes = 28L * 1024 * 1024 * 1024,
            VramKnown = true,
            GpuVendor = vendor,
            GpuAccelAvailable = vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel,
            CpuCores = 16,
            FreeDiskBytes = 200L * 1024 * 1024 * 1024
        };
}
