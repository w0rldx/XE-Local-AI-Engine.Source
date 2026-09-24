namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using Microsoft.Extensions.Logging;
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
[Category(TestCategories.Unit)]
public sealed class WhisperBackendSelectorTests
{
    [Test]
    public void SelectForVendor_WindowsNvidia_ReturnsCuda()
    {
        AssertEx.Equal(WhisperBackend.Cuda, WhisperBackendSelector.SelectForVendor(GpuVendor.Nvidia, isWindows: true, cudaDeviceAvailable: true));
    }

    [Test]
    public void SelectForVendor_LinuxNvidia_ReturnsCpu()
    {
        // The CUDA lane on Linux is the managed source build or the bring-your-own override, both of which
        // short-circuit before this rule is consulted at all.
        AssertEx.Equal(WhisperBackend.Cpu, WhisperBackendSelector.SelectForVendor(GpuVendor.Nvidia, isWindows: false, cudaDeviceAvailable: true));
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
        AssertEx.Equal(WhisperBackend.Cpu, WhisperBackendSelector.SelectForVendor(vendor, isWindows, cudaDeviceAvailable: true));
    }

    [Test]
    public void SelectForVendor_WindowsNvidiaWithoutCudaDevice_ReturnsCpu()
    {
        AssertEx.Equal(WhisperBackend.Cpu, WhisperBackendSelector.SelectForVendor(GpuVendor.Nvidia, isWindows: true, cudaDeviceAvailable: false));
    }

    [Test]
    public async Task SelectBackend_WindowsNvidia_ConsultsCudaProbe_AndSelectsCudaWhenDevicePresent()
    {
        var probe = new FakeCudaDeviceProbe(hasDevice: true);
        var selector = new WhisperBackendSelector(Profiler(GpuVendor.Nvidia), isWindows: true, cudaDeviceProbe: probe);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(WhisperBackend.Cuda, backend);
        AssertEx.Equal(expected: 1, probe.Calls);
    }

    [Test]
    public async Task SelectBackend_WindowsNvidia_ZeroCudaDevices_FallsBackToCpu_AndWarns()
    {
        // The 2026-09-22 tester box: an NVIDIA GPU whose driver enumerated zero CUDA devices. The cuBLAS whisper-server reported
        // ready and died on its first request; CPU is served instead and the reason is stated once at Warning.
        var logger = new RecordingLogger<WhisperBackendSelector>();
        var selector = new WhisperBackendSelector(Profiler(GpuVendor.Nvidia),
            isWindows: true,
            cudaDeviceProbe: new FakeCudaDeviceProbe(hasDevice: false),
            logger: logger);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(WhisperBackend.Cpu, backend);
        AssertEx.True(logger.HasEntry(LogLevel.Warning, "no CUDA device could be enumerated"));
    }

    [Test]
    public async Task Recommend_OnAWindowsNvidiaBoxWithoutCudaDevice_YieldsACpuTierModel()
    {
        // The recommendation is fed by the selector, so the degraded backend must also degrade the recommended model: a
        // 28 GiB card would otherwise be handed a turbo row that then runs on CPU slower than real time.
        var selector = new WhisperBackendSelector(Profiler(GpuVendor.Nvidia), isWindows: true, cudaDeviceProbe: new FakeCudaDeviceProbe(hasDevice: false));

        var backend = await selector.SelectBackendAsync(CancellationToken.None);
        var recommended = WhisperModelRecommendation.Recommend(Profile(GpuVendor.Nvidia), backend);

        AssertEx.True(recommended.Tier <= WhisperModelTier.Small,
            $"A box without an enumerable CUDA device must be recommended a CPU-tier model; got '{recommended.Id}' ({recommended.Tier}).");
    }

    [Test]
    public async Task SelectBackend_CudaFailureLatched_ServesCpuDespiteAPositiveProbe_AndWarnsOnce()
    {
        // A present driver that enumerates zero devices passes the nvcuda.dll probe; only the pinned daemon's death reveals it.
        var signal = new WhisperCudaFailureSignal();
        signal.Set("The CUDA whisper-server exited unexpectedly (exit code -1073740791)");
        var logger = new RecordingLogger<WhisperBackendSelector>();
        var selector = new WhisperBackendSelector(Profiler(GpuVendor.Nvidia),
            isWindows: true,
            cudaDeviceProbe: new FakeCudaDeviceProbe(hasDevice: true),
            logger: logger,
            cudaFailureSignal: signal);

        var first = await selector.SelectBackendAsync(CancellationToken.None);
        var second = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(WhisperBackend.Cpu, first);
        AssertEx.Equal(WhisperBackend.Cpu, second);
        var warnings = logger.Entries.Where(static entry => entry.Level == LogLevel.Warning).ToList();
        AssertEx.Equal(expected: 1, warnings.Count, "The fallback is stated once per process, not on every selection.");
        AssertEx.Contains(warnings[0].Message, "exit code -1073740791", StringComparison.Ordinal);
        AssertEx.Contains(warnings[0].Message, "until the node restarts", StringComparison.Ordinal);
    }

    [Test]
    public async Task Recommend_AfterTheCudaDaemonDied_YieldsACpuTierModel()
    {
        var signal = new WhisperCudaFailureSignal();
        signal.Set("The CUDA whisper-server exited unexpectedly (exit code 1)");
        var selector = new WhisperBackendSelector(Profiler(GpuVendor.Nvidia), isWindows: true, cudaDeviceProbe: new FakeCudaDeviceProbe(hasDevice: true), cudaFailureSignal: signal);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);
        var recommended = WhisperModelRecommendation.Recommend(Profile(GpuVendor.Nvidia), backend);

        AssertEx.True(recommended.Tier <= WhisperModelTier.Small,
            $"A node whose CUDA daemon died must be recommended a CPU-tier model; got '{recommended.Id}' ({recommended.Tier}).");
    }

    [Test]
    public async Task SelectBackend_OverrideActive_WinsOverALatchedCudaFailure()
    {
        var signal = new WhisperCudaFailureSignal();
        signal.Set("The CUDA whisper-server exited unexpectedly");
        var selector = new WhisperBackendSelector(Profiler(GpuVendor.Nvidia),
            isWindows: true,
            new WhisperServerRuntimeOverrideOptions
            {
                ServerPath = @"C:\whisper\whisper-server.exe",
                Backend = WhisperBackend.Cuda
            },
            cudaFailureSignal: signal);

        AssertEx.Equal(WhisperBackend.Cuda, await selector.SelectBackendAsync(CancellationToken.None));
    }

    [Test]
    public async Task SelectBackend_LinuxNvidia_DoesNotConsultCudaProbe()
    {
        var probe = new FakeCudaDeviceProbe(hasDevice: false);
        var selector = new WhisperBackendSelector(Profiler(GpuVendor.Nvidia), isWindows: false, cudaDeviceProbe: probe);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(WhisperBackend.Cpu, backend);
        AssertEx.Equal(expected: 0, probe.Calls);
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

    private static IHardwareProfiler Profiler(GpuVendor vendor)
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Profile(vendor));
        return profiler;
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
