namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The node's CUDA device probe: the cached llama.cpp device audit's verdict first, the driver-library check second.</summary>
/// <remarks>
///     The Windows tester box has nvcuda.dll (so the default probe says "present") while its driver enumerates zero CUDA devices, and the
///     cuBLAS whisper-server crashes on its first inference. The audit already knows that; the probe reads its cached verdict, never computes one.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class RuntimeAuditCudaDeviceProbeTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Test]
    public async Task CachedAudit_CudaBuildSawZeroDevices_RulesCudaOut_AndWarnsOnce()
    {
        using var audit = Audit(GpuVariant.Cuda, LlamaDeviceInventory.Empty(GpuVariant.Cuda));
        await audit.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        var logger = new CapturingLogger<RuntimeAuditCudaDeviceProbe>();
        var probe = new RuntimeAuditCudaDeviceProbe(audit, DriverLibraryPresent(), logger);

        AssertEx.False(probe.HasEnumerableCudaDevice());
        AssertEx.False(probe.HasEnumerableCudaDevice());

        var warnings = logger.AllText.Split("enumerates no CUDA device").Length - 1;
        AssertEx.Equal(1, warnings);
    }

    [Test]
    public void NoCachedAudit_DefersToTheDefaultProbe_WithoutComputingOne()
    {
        var inventory = Substitute.For<ILlamaDeviceInventoryProbe>();
        using var audit = Audit(GpuVariant.Cuda, inventory);
        var probe = new RuntimeAuditCudaDeviceProbe(audit, DriverLibraryPresent(), NullLogger<RuntimeAuditCudaDeviceProbe>.Instance);

        AssertEx.True(probe.HasEnumerableCudaDevice());
        AssertEx.Equal(0, inventory.ReceivedCalls().Count(), "the probe must peek, never run --list-devices");
    }

    [Test]
    public async Task CachedAudit_CudaEnumeratesADevice_DefersToTheDefaultProbe()
    {
        using var audit = Audit(GpuVariant.Cuda, WithDevice(GpuVariant.Cuda));
        await audit.GetAuditAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.True(new RuntimeAuditCudaDeviceProbe(audit, DriverLibraryPresent(), NullLogger<RuntimeAuditCudaDeviceProbe>.Instance).HasEnumerableCudaDevice());
        AssertEx.False(new RuntimeAuditCudaDeviceProbe(audit, new DefaultCudaDeviceProbe(isWindows: true, static () => false), NullLogger<RuntimeAuditCudaDeviceProbe>.Instance)
                           .HasEnumerableCudaDevice(), "a missing driver library still rules CUDA out");
    }

    [Test]
    public async Task IndeterminateAudit_IsNotCached_SoTheDefaultProbeAnswers()
    {
        using var audit = Audit(GpuVariant.Cuda, LlamaDeviceInventory.Unknown(GpuVariant.Cuda));
        await audit.GetAuditAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Null(audit.PeekCached());
        AssertEx.True(new RuntimeAuditCudaDeviceProbe(audit, DriverLibraryPresent(), NullLogger<RuntimeAuditCudaDeviceProbe>.Instance).HasEnumerableCudaDevice());
    }

    [Test]
    [Arguments(GpuVariant.Cpu)]
    [Arguments(GpuVariant.Vulkan)]
    public async Task FallbackOnANonCudaVariant_SaysNothingAboutCuda(GpuVariant variant)
    {
        // A CPU variant on a GPU box, or a Vulkan build without an ICD, is a CPU fallback too, but not evidence against the CUDA driver.
        using var audit = Audit(variant, LlamaDeviceInventory.Empty(variant));
        var state = await audit.GetAuditAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.True(state.CpuFallback);
        AssertEx.False(RuntimeAuditCudaDeviceProbe.CudaEnumeratesNoDevice(state));
    }

    [Test]
    public async Task WhisperSelector_OnWindowsNvidia_PicksCpu_WhenTheAuditRulesCudaOut()
    {
        using var audit = Audit(GpuVariant.Cuda, LlamaDeviceInventory.Empty(GpuVariant.Cuda));
        await audit.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        var probe = new RuntimeAuditCudaDeviceProbe(audit, DriverLibraryPresent(), NullLogger<RuntimeAuditCudaDeviceProbe>.Instance);
        var selector = new WhisperBackendSelector(Profiler(), isWindows: true, cudaDeviceProbe: probe);

        AssertEx.Equal(WhisperBackend.Cpu, await selector.SelectBackendAsync(CancellationToken.None));
    }

    private static DefaultCudaDeviceProbe DriverLibraryPresent() => new(isWindows: true, static () => true);

    private static RuntimeDeviceAuditService Audit(GpuVariant variant, LlamaDeviceInventory inventory)
    {
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(inventory));
        return Audit(variant, probe);
    }

    private static RuntimeDeviceAuditService Audit(GpuVariant variant, ILlamaDeviceInventoryProbe probe)
    {
        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(variant));
        return new RuntimeDeviceAuditService(Profiler(), selector, probe, NullLogger<RuntimeDeviceAuditService>.Instance);
    }

    private static LlamaDeviceInventory WithDevice(GpuVariant variant) =>
        new() { Variant = variant, ProbeSucceeded = true, Devices = [new LlamaGpuDevice { Name = "GPU0", TotalBytes = 16 * Gb, FreeBytes = 15 * Gb }] };

    private static IHardwareProfiler Profiler()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = 16 * Gb,
            AvailableVramBytes = 15 * Gb,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        }));
        return profiler;
    }
}
