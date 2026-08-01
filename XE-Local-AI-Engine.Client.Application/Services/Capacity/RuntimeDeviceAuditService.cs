namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IRuntimeDeviceAudit" />. Composes the host hardware profile (<see cref="IHardwareProfiler" />),
///     the selected acceleration variant (<see cref="IGpuVariantSelector" />), and the devices that variant's binary
///     actually enumerates (<see cref="ILlamaDeviceInventoryProbe" />) into a node-level audit that flags a silent CPU
///     fallback. The expensive part — the <c>--list-devices</c> probe — is cached in the probe per binary, and the audit
///     memoizes its computed state, so a warm inference path never pays for it. Only a DETERMINATE audit is memoized:
///     an indeterminate device probe (timeout / spawn failure) is returned uncached so the next call re-probes instead
///     of pinning "unknown" — and its phantom-VRAM trust — until restart or a forced refresh. The device-fallback
///     warning + counter fire once per state change (i.e. once per binary), not per call.
/// </summary>
public sealed class RuntimeDeviceAuditService : IRuntimeDeviceAudit, IDisposable
{
    private readonly ILlamaDeviceInventoryProbe _deviceProbe;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly ILogger<RuntimeDeviceAuditService> _logger;
    private readonly IGpuVariantSelector _variantSelector;
    private readonly ICudaManagedBuildSignal? _managedCudaSignal;
    private readonly ILlamaLayerPlacementReport? _layerPlacementReport;

    // Serializes the (rare) first compute + any force-refresh so a concurrent burst runs the device probe at most once.
    private readonly SemaphoreSlim _computeGate = new(initialCount: 1, maxCount: 1);
    private volatile RuntimeDeviceAuditState? _cached;

    // The managed-CUDA signal stamp the cached audit was computed against. A CUDA adopt/remove bumps the signal version
    // and can flip the selected variant (Vulkan↔Cuda on a Linux NVIDIA box), so a memo computed against the old stamp is
    // stale — the fast path only trusts the cache while the current stamp matches.
    private long _cachedSignalVersion;
    private string? _lastEmittedSignature;

    public RuntimeDeviceAuditService(IHardwareProfiler hardwareProfiler,
        IGpuVariantSelector variantSelector,
        ILlamaDeviceInventoryProbe deviceProbe,
        ILogger<RuntimeDeviceAuditService> logger,
        ICudaManagedBuildSignal? managedCudaSignal = null,
        ILlamaLayerPlacementReport? layerPlacementReport = null)
    {
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _deviceProbe = deviceProbe ?? throw new ArgumentNullException(nameof(deviceProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Optional so the test seam (and a provider-only host) can omit it; when absent the stamp is a constant, so the
        // memo behaves exactly as before (no signal-driven invalidation). The composition root injects the real singleton.
        _managedCudaSignal = managedCudaSignal;

        // Also optional, and read live on every audit rather than memoized — see RuntimeDeviceAuditState.LayerPlacement.
        _layerPlacementReport = layerPlacementReport;
    }

    /// <summary>Disposes the compute gate. Invoked by the container on shutdown (the service is a singleton).</summary>
    public void Dispose()
    {
        _computeGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<RuntimeDeviceAuditState> GetAuditAsync(bool forceRefresh, CancellationToken ct)
    {
        // The cache is trusted only while the managed-CUDA signal stamp is unchanged from the one it was computed
        // against; an adopt/remove bumps the stamp and can flip the selected variant, so a mismatch forces a re-compute.
        if (!forceRefresh && _cached is { } cached && CurrentSignalVersion() == Volatile.Read(ref _cachedSignalVersion))
        {
            return WithLivePlacement(cached);
        }

        await _computeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — a concurrent caller may have computed it while we waited.
            if (!forceRefresh && _cached is { } current && CurrentSignalVersion() == Volatile.Read(ref _cachedSignalVersion))
            {
                return WithLivePlacement(current);
            }

            var (state, fallbackReasonCode, determinate, signalVersion) = await ComputeAsync(ct).ConfigureAwait(false);

            // Latch only a determinate audit (the device probe ran, or a CPU variant that needs no probe). An
            // indeterminate probe yields backend "unknown" with CpuFallback:false — memoizing that would keep
            // capacity/advisor trusting the raw profile's VRAM until restart or a forced refresh. The probe layer
            // deliberately does not cache failed probes, so returning this uncached makes the next call a real re-probe.
            // The stamp is captured inside ComputeAsync just before the variant is selected, so a signal flip during the
            // compute leaves the cached stamp behind the current one and the next call re-computes.
            if (determinate)
            {
                _cached = state;
                Volatile.Write(ref _cachedSignalVersion, signalVersion);
            }

            EmitIfFallbackChanged(state, fallbackReasonCode);
            return WithLivePlacement(state);
        }
        finally
        {
            _computeGate.Release();
        }
    }

    /// <summary>
    ///     Stamps the CURRENT measured layer placement onto an audit. The device audit is memoized per binary, but
    ///     placement changes every time a different model loads, so it must never be frozen into the memo — the memo
    ///     stores the device decision alone and this re-reads the live report on the way out.
    /// </summary>
    private RuntimeDeviceAuditState WithLivePlacement(RuntimeDeviceAuditState state)
    {
        var placement = _layerPlacementReport?.Current;
        return placement is null && state.LayerPlacement is null
            ? state
            : state with
            {
                LayerPlacement = placement
            };
    }

    private long CurrentSignalVersion()
    {
        return _managedCudaSignal?.Version ?? 0;
    }

    /// <inheritdoc />
    public async Task<HardwareProfile> GetEffectiveProfileAsync(bool forceRefreshProfile, CancellationToken ct)
    {
        var raw = await _hardwareProfiler.GetProfileAsync(forceRefreshProfile, ct).ConfigureAwait(false);
        var audit = await GetAuditAsync(forceRefresh: false, ct).ConfigureAwait(false);
        if (!audit.CpuFallback)
        {
            return raw;
        }

        // The GPU is present but the selected runtime cannot use it — size against system RAM, never phantom VRAM. This
        // is exactly the profile's own documented CPU-mode floor (VramKnown:false ⇒ GpuAccelAvailable:false).
        return raw with
        {
            VramKnown = false,
            GpuAccelAvailable = false,
            VramBytes = null,
            AvailableVramBytes = null
        };
    }

    private async Task<(RuntimeDeviceAuditState State, string? FallbackReasonCode, bool Determinate, long SignalVersion)> ComputeAsync(CancellationToken ct)
    {
        // The raw profile is read non-force here (the audit only needs the vendor / total-VRAM presence, not a live free
        // figure); the free figures are re-probed by GetEffectiveProfileAsync when a caller needs them live.
        var raw = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);

        // Capture the managed-CUDA signal stamp immediately BEFORE selecting the variant (the selector reads the signal):
        // this is the stamp the resulting audit is valid for. If the signal flips after this read, the cached stamp lags
        // the current one and the next GetAuditAsync re-computes rather than trusting a memo built against the old state.
        var signalVersion = CurrentSignalVersion();
        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var inventory = await _deviceProbe.GetDeviceInventoryAsync(variant, ct).ConfigureAwait(false);

        var state = BuildState(raw, variant, inventory);
        string? reasonCode = null;
        if (state.CpuFallback)
        {
            reasonCode = variant == GpuVariant.Cpu ? "cpu_variant" : "zero_devices";
        }

        // Determinate = safe to memoize: the audit for a CPU variant never depends on the probe, and a GPU variant's
        // audit is only trustworthy when the probe actually ran.
        var determinate = variant == GpuVariant.Cpu || inventory.ProbeSucceeded;
        return (state, reasonCode, determinate, signalVersion);
    }

    /// <summary>Pure audit decision over (host profile, selected variant, enumerated devices) — unit-testable without I/O.</summary>
    internal static RuntimeDeviceAuditState BuildState(HardwareProfile raw, GpuVariant variant, LlamaDeviceInventory inventory)
    {
        // A usable GPU is advertised: a vendor GPU with a known, positive total VRAM.
        var gpuExpected = raw.GpuVendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel && (raw.VramBytes ?? 0) > 0;

        // CPU fallback = GPU expected AND the runtime runs on the CPU: a CPU variant, or a GPU variant that RAN and saw
        // zero devices. An indeterminate probe (ProbeSucceeded == false) is "unknown" and never raises a false alarm.
        var cpuVariant = variant == GpuVariant.Cpu;
        var zeroDevices = !cpuVariant && inventory.ProbeSucceeded && inventory.Devices.Count == 0;
        var cpuFallback = gpuExpected && (cpuVariant || zeroDevices);

        var (reason, remediation) = cpuFallback ? BuildFallbackText(raw.GpuVendor, variant, cpuVariant) : (null, (string?)null);

        var backend = ResolveInferenceBackend(variant, inventory);
        return new RuntimeDeviceAuditState
        {
            InferenceBackend = backend,
            GpuExpected = gpuExpected,
            CpuFallback = cpuFallback,
            Reason = reason,
            Remediation = remediation,
            BackendUndeterminedReason = backend == "unknown" ? BuildUndeterminedText(variant) : null,
            Devices = [.. inventory.Devices.Select(static device => new RuntimeAuditDevice(device.Name, device.TotalBytes, device.FreeBytes))]
        };
    }

    // The device probe neither succeeded nor proved a fallback. Everything downstream — the capacity gate, the model
    // advisor's VRAM budget — is sized against a GPU nobody confirmed is reachable, so the operator has to be told
    // that this is an unanswered question rather than a clean bill of health.
    private static string BuildUndeterminedText(GpuVariant variant)
    {
        return $"The {VariantName(variant)} llama.cpp runtime is selected, but listing its GPU devices did not complete "
               + "(the probe timed out or the binary could not be started), so whether inference will use the GPU is unknown. "
               + "Model sizing on this page still assumes the GPU's VRAM is usable. A wedged or busy GPU driver is the usual "
               + "cause; refreshing the hardware profile re-runs the probe.";
    }

    // The backend inference actually runs on: a GPU variant that enumerated devices is that variant; a GPU variant with
    // zero devices is an effective "cpu"; a GPU variant whose probe could not run is "unknown"; a CPU variant is "cpu".
    private static string ResolveInferenceBackend(GpuVariant variant, LlamaDeviceInventory inventory)
    {
        if (variant == GpuVariant.Cpu)
        {
            return "cpu";
        }

        if (!inventory.ProbeSucceeded)
        {
            return "unknown";
        }

        if (inventory.Devices.Count == 0)
        {
            return "cpu";
        }

        return variant == GpuVariant.Cuda ? "cuda" : "vulkan";
    }

    private static (string Reason, string? Remediation) BuildFallbackText(GpuVendor vendor, GpuVariant variant, bool cpuVariant)
    {
        var reason = cpuVariant
            ? $"A {VendorName(vendor)} GPU was detected, but the CPU llama.cpp runtime is selected — inference is running on the CPU."
            : $"The {VariantName(variant)} llama.cpp runtime is selected but enumerated no GPU devices (commonly a missing Vulkan ICD under WSL2), so inference is silently running on the CPU.";

        const string Remediation =
            "To run on the GPU: build the CUDA runtime from source with the in-app build feature, or set XE_LLAMACPP_SERVER_PATH + "
            + "XE_LLAMACPP_VARIANT to a GPU-capable llama-server binary. On Linux there is no prebuilt CUDA llama.cpp — the default "
            + "NVIDIA build is Vulkan, which needs a Vulkan ICD; if a bring-your-own override is set, verify XE_LLAMACPP_SERVER_PATH "
            + "points to a valid GPU-capable binary.";

        return (reason, Remediation);
    }

    private static string VendorName(GpuVendor vendor)
    {
        return vendor switch
        {
            GpuVendor.Nvidia => "NVIDIA",
            GpuVendor.Amd => "AMD",
            GpuVendor.Intel => "Intel",
            _ => "GPU"
        };
    }

    private static string VariantName(GpuVariant variant)
    {
        return variant switch
        {
            GpuVariant.Cuda => "CUDA",
            GpuVariant.Vulkan => "Vulkan",
            _ => "CPU"
        };
    }

    private void EmitIfFallbackChanged(RuntimeDeviceAuditState state, string? fallbackReasonCode)
    {
        // Fire the warning + counter at most once per distinct (fallback, backend) — the audit is cached per binary, so a
        // change here means the binary (or its device enumeration) changed, which is exactly when an operator should hear.
        var signature = string.Concat(state.CpuFallback ? "1" : "0", "|", state.InferenceBackend);
        if (string.Equals(signature, _lastEmittedSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastEmittedSignature = signature;
        if (!state.CpuFallback)
        {
            if (state.BackendUndeterminedReason is { } undetermined)
            {
                _logger.LogWarning("Runtime device audit could not determine the inference backend: {Reason}", undetermined);
            }

            return;
        }

        NodeMetrics.DeviceFallbackTotal.Add(1, new KeyValuePair<string, object?>("reason", fallbackReasonCode ?? "unknown"));
        _logger.LogWarning("Runtime device audit detected a CPU fallback (backend {Backend}): {Reason} {Remediation}",
            state.InferenceBackend, state.Reason, state.Remediation);
    }
}
