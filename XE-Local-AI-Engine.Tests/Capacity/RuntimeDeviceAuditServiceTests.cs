namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The runtime device audit (AUD4-03). Its pure <see cref="RuntimeDeviceAuditService.BuildState" /> decides a silent
///     CPU fallback from (host profile, selected variant, enumerated devices): a GPU box whose runtime enumerates the GPU
///     is fine; a CPU variant on a GPU box, or a GPU variant that RAN and saw zero devices, is a fallback; an
///     indeterminate probe never raises a false alarm; and a CPU-only host is never flagged. The effective-profile
///     projection degrades a fallback box to CPU-mode so the advisor + capacity gate size against RAM, not phantom VRAM.
/// </summary>
public sealed class RuntimeDeviceAuditServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Test]
    public void BuildState_GpuExpected_GpuVariantWithDevices_NoFallback()
    {
        var state = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, WithDevices(GpuVariant.Cuda));

        AssertEx.True(state.GpuExpected);
        AssertEx.False(state.CpuFallback);
        AssertEx.Equal("cuda", state.InferenceBackend);
        AssertEx.Null(state.Reason);
    }

    [Test]
    public void BuildState_GpuExpected_GpuVariantZeroDevices_IsFallback_AndNamesLikelyCause()
    {
        // The audited WSL2 case: the Vulkan build ran --list-devices and saw nothing (no ICD) — a silent CPU fallback.
        var state = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Vulkan, LlamaDeviceInventory.Empty(GpuVariant.Vulkan));

        AssertEx.True(state.CpuFallback);
        AssertEx.Equal("cpu", state.InferenceBackend);
        AssertEx.True(state.Reason!.Contains("Vulkan", StringComparison.Ordinal));
        AssertEx.True(state.Reason!.Contains("WSL2", StringComparison.Ordinal));
        AssertEx.True(state.Remediation!.Contains("build", StringComparison.OrdinalIgnoreCase));
        AssertEx.True(state.Remediation!.Contains("XE_LLAMACPP_SERVER_PATH", StringComparison.Ordinal));
    }

    [Test]
    public void BuildState_GpuExpected_CpuVariantSelected_IsFallback()
    {
        var state = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cpu, LlamaDeviceInventory.Empty(GpuVariant.Cpu));

        AssertEx.True(state.CpuFallback);
        AssertEx.Equal("cpu", state.InferenceBackend);
        AssertEx.True(state.Reason!.Contains("CPU", StringComparison.Ordinal));
    }

    [Test]
    public void BuildState_CpuOnlyHost_NoFalseAlarm()
    {
        var cpuProfile = new HardwareProfile
        {
            TotalRamBytes = 32 * Gb,
            AvailableRamBytes = 24 * Gb,
            VramBytes = null,
            AvailableVramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.None,
            GpuAccelAvailable = false,
            CpuCores = 8,
            FreeDiskBytes = 100 * Gb
        };

        var state = RuntimeDeviceAuditService.BuildState(cpuProfile, GpuVariant.Cpu, LlamaDeviceInventory.Empty(GpuVariant.Cpu));

        AssertEx.False(state.GpuExpected);
        AssertEx.False(state.CpuFallback);
        AssertEx.Equal("cpu", state.InferenceBackend);
    }

    [Test]
    public void BuildState_GpuExpected_ProbeIndeterminate_NoFalseAlarm_BackendUnknown()
    {
        // The probe could not run (timeout / spawn failure) — never treat "no devices seen" as "no GPU".
        var state = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Vulkan, LlamaDeviceInventory.Unknown(GpuVariant.Vulkan));

        AssertEx.True(state.GpuExpected);
        AssertEx.False(state.CpuFallback);
        AssertEx.Equal("unknown", state.InferenceBackend);
    }

    [Test]
    public async Task GetEffectiveProfile_OnFallback_DegradesToCpuMode()
    {
        // BYO-override-style mismatch / WSL2 Vulkan: raw profile advertises a 16 GB NVIDIA GPU but the runtime sees no
        // devices, so the effective profile the advisor/capacity gate consume must be CPU-mode (VRAM unknown).
        var raw = GpuProfile(GpuVendor.Nvidia);
        using var service = BuildService(raw, GpuVariant.Vulkan, LlamaDeviceInventory.Empty(GpuVariant.Vulkan));

        var effective = await service.GetEffectiveProfileAsync(forceRefreshProfile: false, CancellationToken.None);

        AssertEx.False(effective.VramKnown);
        AssertEx.False(effective.GpuAccelAvailable);
        AssertEx.Null(effective.VramBytes);
        AssertEx.Null(effective.AvailableVramBytes);
        // System RAM is preserved so CPU-mode sizing has a budget.
        AssertEx.Equal(raw.AvailableRamBytes, effective.AvailableRamBytes);
    }

    [Test]
    public async Task GetEffectiveProfile_WhenGpuWorking_ReturnsRawUnchanged()
    {
        var raw = GpuProfile(GpuVendor.Nvidia);
        using var service = BuildService(raw, GpuVariant.Cuda, WithDevices(GpuVariant.Cuda));

        var effective = await service.GetEffectiveProfileAsync(forceRefreshProfile: false, CancellationToken.None);

        AssertEx.True(effective.VramKnown);
        AssertEx.True(effective.GpuAccelAvailable);
        AssertEx.Equal(raw.VramBytes, effective.VramBytes);
    }

    [Test]
    public async Task GetAudit_IsMemoized_ProbesOnce()
    {
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(WithDevices(GpuVariant.Cuda)));
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, probe);

        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);

        await probe.Received(1).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    private static HardwareProfile GpuProfile(GpuVendor vendor)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = 16 * Gb,
            AvailableVramBytes = 15 * Gb,
            VramKnown = true,
            GpuVendor = vendor,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    private static LlamaDeviceInventory WithDevices(GpuVariant variant)
    {
        return new LlamaDeviceInventory
        {
            Variant = variant,
            ProbeSucceeded = true,
            Devices = [new LlamaGpuDevice("GPU0", 16 * Gb, 15 * Gb)]
        };
    }

    private static RuntimeDeviceAuditService BuildService(HardwareProfile raw, GpuVariant variant, LlamaDeviceInventory inventory)
    {
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(inventory));
        return BuildService(raw, variant, probe);
    }

    private static RuntimeDeviceAuditService BuildService(HardwareProfile raw, GpuVariant variant, ILlamaDeviceInventoryProbe probe)
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(raw));

        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(variant));

        return new RuntimeDeviceAuditService(profiler, selector, probe, NullLogger<RuntimeDeviceAuditService>.Instance);
    }
}
