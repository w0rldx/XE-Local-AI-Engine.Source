namespace XE_Local_AI_Engine.Tests.Capacity;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The runtime device audit. Its pure <see cref="RuntimeDeviceAuditService.BuildState" /> decides a silent
///     CPU fallback from (host profile, selected variant, enumerated devices): a GPU box whose runtime enumerates the GPU
///     is fine; a CPU variant on a GPU box, or a GPU variant that RAN and saw zero devices, is a fallback; an
///     indeterminate probe never raises a false alarm; and a CPU-only host is never flagged. The effective-profile
///     projection degrades a fallback box to CPU-mode so the advisor + capacity gate size against RAM, not phantom VRAM.
///     The audit also never ACQUIRES a runtime: with none installed it answers "undetermined, nothing installed yet"
///     without touching the binary manager's acquisition surface, and recovers on the first call after an install.
/// </summary>
[Category(TestCategories.Unit)]
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
        AssertEx.True(state.Reason.Contains("WSL2", StringComparison.Ordinal));
        AssertEx.True(state.Remediation!.Contains("build", StringComparison.OrdinalIgnoreCase));
        AssertEx.True(state.Remediation.Contains("XE_LLAMACPP_SERVER_PATH", StringComparison.Ordinal));
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

        // "unknown" must not be silent. Without an operator-facing explanation a wedged driver or an overrun probe is
        // indistinguishable from health: CpuFallback stays false (correctly) and the UI would show nothing at all.
        var undetermined = AssertEx.NotNull(state.BackendUndeterminedReason);
        AssertEx.True(undetermined.Length > 0);
    }

    [Test]
    public void BuildState_UndeterminedReason_NamesTheOverrideCause_AndAssertsNoSingleCause()
    {
        // Regression pin for a wrong diagnosis measured on Windows 11 2026-08-03. The most reachable route to
        // "undetermined" is an XE_LLAMACPP_SERVER_PATH override that LlamaCppBinaryManager deliberately refuses because
        // it exposes no GPU device — NOT a timeout, NOT a failed start, and NOT a wedged driver. The old text asserted
        // all three and sent the operator to diagnose a healthy driver.
        var state = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, LlamaDeviceInventory.Unknown(GpuVariant.Cuda));

        var undetermined = AssertEx.NotNull(state.BackendUndeterminedReason);

        // The actionable cause must be named.
        AssertEx.True(undetermined.Contains("XE_LLAMACPP_SERVER_PATH", StringComparison.Ordinal),
            "the undetermined reason must name the override, which is the most reachable cause");

        // The negative control: it must no longer assert a cause it cannot know.
        AssertEx.False(undetermined.Contains("wedged", StringComparison.OrdinalIgnoreCase),
            "the undetermined reason must not blame a wedged driver as though it were the known cause");
        AssertEx.False(undetermined.Contains("the probe timed out or the binary could not be started", StringComparison.Ordinal),
            "the undetermined reason must not assert a timeout/start failure it did not observe");
    }

    [Test]
    public void BuildState_RuntimeNotInstalled_SaysSo_InsteadOfBlamingADriverOrAnOverride()
    {
        // A brand-new node has no llama.cpp runtime yet and the device probe deliberately does not fetch one, so
        // "undetermined" is the ordinary first-boot state. Reusing the malfunction text would send that operator to
        // diagnose a healthy driver or an override they never configured — the same wrong-diagnosis failure the
        // override wording above was written to fix.
        var state = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Vulkan, LlamaDeviceInventory.RuntimeNotInstalled(GpuVariant.Vulkan));

        AssertEx.False(state.CpuFallback, "nothing was probed, so nothing proves a CPU fallback");
        AssertEx.Equal("unknown", state.InferenceBackend);

        var undetermined = AssertEx.NotNull(state.BackendUndeterminedReason);
        AssertEx.True(undetermined.Contains("No llama.cpp runtime is installed yet", StringComparison.Ordinal),
            "the operator must be told the runtime is simply not installed yet");
        AssertEx.False(undetermined.Contains("XE_LLAMACPP_SERVER_PATH", StringComparison.Ordinal),
            "a node with nothing installed must not be sent to debug a bring-your-own override");
        AssertEx.False(undetermined.Contains("driver", StringComparison.OrdinalIgnoreCase),
            "a node with nothing installed must not be sent to debug a GPU driver");
    }

    [Test]
    public async Task GetAudit_RuntimeNotInstalled_IsNotLatched_AndConvergesOnceInstalled()
    {
        // The caching trap in its own right: "unknown because nothing is installed" must not survive the install. The
        // audit memoizes only a determinate pass, so the first call after the ensure/select endpoint (or first-run
        // provisioning, or a source-build adoption) lands on the real device list without a forced refresh.
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(LlamaDeviceInventory.RuntimeNotInstalled(GpuVariant.Cuda)),
                 Task.FromResult(WithDevices(GpuVariant.Cuda)));
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, probe);

        var beforeInstall = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        AssertEx.Equal("unknown", beforeInstall.InferenceBackend);

        var afterInstall = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        AssertEx.Equal("cuda", afterInstall.InferenceBackend);
        AssertEx.Null(afterInstall.BackendUndeterminedReason);
    }

    [Test]
    public async Task GetAudit_NoRuntimeInstalled_AnswersUndetermined_WithoutEverAcquiringOne()
    {
        // The whole chain GetHardwareProfileEndpoint drives, with the REAL device probe: a page-load GET must not have a
        // multi-hundred-megabyte side effect on any external-access profile. EnsureBinaryAsync is the tripwire.
        var binaryManager = NoRuntimeInstalled();
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia),
            GpuVariant.Vulkan,
            new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance));

        var state = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);

        AssertEx.Equal("unknown", state.InferenceBackend);
        AssertEx.False(state.CpuFallback);
        AssertEx.True(AssertEx.NotNull(state.BackendUndeterminedReason).Contains("No llama.cpp runtime is installed yet", StringComparison.Ordinal));
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }

    [Test]
    public async Task GetEffectiveProfile_TranscriptionStatusPath_NoRuntimeInstalled_AcquiresNothing()
    {
        // TranscriptionRuntimeService.GetRecommendedModelAsync — behind GET transcription/runtime, which the
        // Transcription page loads — reaches the llama.cpp device probe through exactly this call. It must be as inert
        // as the hardware-profile GET, and must still return a usable profile to size the whisper recommendation with.
        var binaryManager = NoRuntimeInstalled();
        var raw = GpuProfile(GpuVendor.Nvidia);
        using var service = BuildService(raw,
            GpuVariant.Vulkan,
            new LlamaDeviceInventoryProbe(binaryManager, NullLogger<LlamaDeviceInventoryProbe>.Instance));

        var effective = await service.GetEffectiveProfileAsync(forceRefreshProfile: false, CancellationToken.None);

        // Unknown is not a proven fallback, so the raw profile stands (unchanged behaviour for an indeterminate probe).
        AssertEx.Equal(raw.VramBytes, effective.VramBytes);
        AssertEx.Equal(raw.AvailableRamBytes, effective.AvailableRamBytes);
        await binaryManager.DidNotReceiveWithAnyArgs().EnsureBinaryAsync(default, default);
    }

    /// <summary>A binary manager with nothing on disk whose acquisition surface fails the test the moment it is used.</summary>
    private static ILlamaCppBinaryManager NoRuntimeInstalled()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.TryGetInstalledBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<LlamaBinary?>(null));
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns<Task<LlamaBinary>>(_ => throw new InvalidOperationException("A device audit must never acquire a llama.cpp runtime."));
        return binaryManager;
    }

    [Test]
    public void BuildState_WhenBackendIsKnown_CarriesNoUndeterminedReason()
    {
        var working = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, WithDevices(GpuVariant.Cuda));
        AssertEx.Null(working.BackendUndeterminedReason);

        var fallback = RuntimeDeviceAuditService.BuildState(GpuProfile(GpuVendor.Nvidia), GpuVariant.Vulkan, LlamaDeviceInventory.Empty(GpuVariant.Vulkan));
        AssertEx.True(fallback.CpuFallback);
        AssertEx.Null(fallback.BackendUndeterminedReason);
    }

    [Test]
    public async Task GetAudit_StampsLayerPlacement_AndTracksItAcrossTheMemoizedAudit()
    {
        // Placement changes as models load, while the device audit is memoized per binary. A placement frozen into the
        // memo would report the first model that ever loaded, forever.
        var report = new LlamaLayerPlacementReport();
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, WithDevices(GpuVariant.Cuda), report);

        var beforeAnyLoad = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        AssertEx.Null(beforeAnyLoad.LayerPlacement);

        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 38, totalLayers: 49);

        var afterLoad = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        var placement = AssertEx.NotNull(afterLoad.LayerPlacement);
        AssertEx.Equal("qwen3-14b", placement.ModelName);
        AssertEx.Equal(expected: 38, placement.OffloadedLayers);
        AssertEx.Equal(expected: 49, placement.TotalLayers);

        // A partial offload is NOT a CPU fallback — the GPU is in use, just not for every layer. Conflating them would
        // make the existing fallback banner claim the GPU is unused.
        AssertEx.False(afterLoad.CpuFallback);
        AssertEx.Equal("cuda", afterLoad.InferenceBackend);
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
    public async Task GetAudit_IndeterminateProbe_IsNotLatched_NextCallReprobesAndConverges()
    {
        // First probe times out / fails to spawn (indeterminate). Latching that would keep capacity/advisor trusting
        // the raw profile's phantom VRAM until restart or a forced refresh — so it must be returned UNCACHED, and a
        // plain (non-force) follow-up call must re-probe and converge on the real answer.
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(LlamaDeviceInventory.Unknown(GpuVariant.Cuda)),
                 Task.FromResult(WithDevices(GpuVariant.Cuda)));
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, probe);

        var first = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        AssertEx.Equal("unknown", first.InferenceBackend);

        var second = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        AssertEx.Equal("cuda", second.InferenceBackend);

        // The determinate result IS memoized as before: a third call answers from the cache.
        var third = await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        AssertEx.Equal("cuda", third.InferenceBackend);
        await probe.Received(2).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
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

    private static RuntimeDeviceAuditService BuildService(HardwareProfile raw,
        GpuVariant variant,
        LlamaDeviceInventory inventory,
        ILlamaLayerPlacementReport? layerPlacementReport = null)
    {
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(inventory));
        return BuildService(raw, variant, probe, layerPlacementReport: layerPlacementReport);
    }

    private static RuntimeDeviceAuditService BuildService(HardwareProfile raw,
        GpuVariant variant,
        ILlamaDeviceInventoryProbe probe,
        ICudaManagedBuildSignal? signal = null,
        ILlamaLayerPlacementReport? layerPlacementReport = null)
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(raw));

        var selector = Substitute.For<IGpuVariantSelector>();
        selector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(variant));

        return new RuntimeDeviceAuditService(profiler, selector, probe, NullLogger<RuntimeDeviceAuditService>.Instance, signal, layerPlacementReport);
    }

    [Test]
    public async Task GetAudit_ManagedCudaSignalFlips_InvalidatesMemo_AndReprobes()
    {
        // The cached audit is keyed to the managed-CUDA signal stamp. A CUDA adopt/remove bumps the stamp and
        // can flip the selected variant (Vulkan↔Cuda on a Linux NVIDIA box), so a plain (non-force) call after the flip
        // must recompute rather than return the stale placement truth. Without the invalidation the second call would
        // answer from the first memo and never re-probe.
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(WithDevices(GpuVariant.Cuda)));
        var signal = new CudaManagedBuildSignal();
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, probe, signal);

        // First determinate compute is memoized (one probe).
        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        await probe.Received(1).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());

        // A CUDA remove flips the signal → the memo is no longer trusted → the next plain call re-probes.
        signal.Clear();
        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        await probe.Received(2).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());

        // The re-computed determinate audit is memoized again against the new stamp — a follow-up call does not re-probe.
        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        await probe.Received(2).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetAudit_IndeterminateProbe_StillNeverCached_EvenWithSignalPresent()
    {
        // The signal stamp must not accidentally start caching an indeterminate probe: an unknown result stays uncached
        // so the next call re-probes, exactly as without a signal.
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(LlamaDeviceInventory.Unknown(GpuVariant.Cuda)));
        var signal = new CudaManagedBuildSignal();
        signal.MarkAvailable();
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia), GpuVariant.Cuda, probe, signal);

        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);
        await service.GetAuditAsync(forceRefresh: false, CancellationToken.None);

        await probe.Received(2).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetAudit_ActiveSourceVariantReplacement_InvalidatesMemoEvenWhenNotCuda()
    {
        var probe = Substitute.For<ILlamaDeviceInventoryProbe>();
        probe.GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(WithDevices(GpuVariant.Vulkan)));
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cpu);
        using var service = BuildService(GpuProfile(GpuVendor.Nvidia), GpuVariant.Vulkan, probe, signal);

        await service.GetAuditAsync(false, CancellationToken.None);
        await service.GetAuditAsync(false, CancellationToken.None);
        await probe.Received(1).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());

        signal.SetActive(GpuVariant.Vulkan);
        await service.GetAuditAsync(false, CancellationToken.None);
        await probe.Received(2).GetDeviceInventoryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }
}
