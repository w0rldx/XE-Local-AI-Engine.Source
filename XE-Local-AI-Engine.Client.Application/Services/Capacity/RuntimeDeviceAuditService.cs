namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IRuntimeDeviceAudit" />: composes the host profile (<see cref="IHardwareProfiler" />), the selected variant
///     (<see cref="IGpuVariantSelector" />) and the devices it enumerates (<see cref="ILlamaDeviceInventoryProbe" />) into a node-level audit.
/// </summary>
/// <remarks>
///     The expensive <c>--list-devices</c> probe is cached in the probe per binary and the audit memoizes its computed state, so a warm
///     inference path never pays for it. Only a DETERMINATE audit is memoized: an indeterminate probe (timeout or spawn failure) is returned
///     uncached, so the next call re-probes instead of pinning "unknown" — and its phantom-VRAM trust — until restart or a forced refresh.
///     The device-fallback warning and counter fire once per state change, which is once per binary, never per call.
/// </remarks>
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

    // The managed-CUDA signal stamp the cached audit was computed against: a CUDA adopt/remove bumps the version and can flip the selected variant
    // (Vulkan↔Cuda on a Linux NVIDIA box), so a memo built against the old stamp is stale and the fast path trusts it only while the stamps match.
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

        await _computeGate.WaitAsync(ct);
        try
        {
            // Re-check under the gate — a concurrent caller may have computed it while we waited.
            if (!forceRefresh && _cached is { } current && CurrentSignalVersion() == Volatile.Read(ref _cachedSignalVersion))
            {
                return WithLivePlacement(current);
            }

            var (state, fallbackReasonCode, determinate, signalVersion) = await ComputeAsync(ct);

            // Latch only a determinate audit: an indeterminate probe yields backend "unknown" with CpuFallback false, and memoizing it would keep capacity
            // and the advisor trusting the raw profile's VRAM until restart — the probe layer caches no failed probe, so leaving it uncached re-probes.
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

    /// <summary>Stamps the CURRENT measured layer placement onto an audit.</summary>
    /// <remarks>
    ///     The device audit is memoized per binary, but placement changes every time a different model loads, so it must never be frozen into
    ///     the memo: the memo stores the device decision alone and this re-reads the live report on the way out.
    /// </remarks>
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
        var raw = await _hardwareProfiler.GetProfileAsync(forceRefreshProfile, ct);
        var audit = await GetAuditAsync(forceRefresh: false, ct);
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

    private async Task<DeviceAuditComputation> ComputeAsync(CancellationToken ct)
    {
        // The raw profile is read non-force here (the audit only needs the vendor / total-VRAM presence, not a live free
        // figure); the free figures are re-probed by GetEffectiveProfileAsync when a caller needs them live.
        var raw = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct);

        // Capture the managed-CUDA signal stamp immediately BEFORE selecting the variant (the selector reads the signal): that is the stamp this audit
        // is valid for, so a flip after this read leaves the cached stamp lagging and the next GetAuditAsync re-computes instead of trusting the memo.
        var signalVersion = CurrentSignalVersion();
        var variant = await _variantSelector.SelectVariantAsync(ct);
        var inventory = await _deviceProbe.GetDeviceInventoryAsync(variant, ct);

        var state = BuildState(raw, variant, inventory);
        string? reasonCode = null;
        if (state.CpuFallback)
        {
            reasonCode = variant == GpuVariant.Cpu ? "cpu_variant" : "zero_devices";
        }

        // Determinate = safe to memoize: the audit for a CPU variant never depends on the probe, and a GPU variant's
        // audit is only trustworthy when the probe actually ran.
        var determinate = variant == GpuVariant.Cpu || inventory.ProbeSucceeded;
        return new DeviceAuditComputation(state, reasonCode, determinate, signalVersion);
    }

    /// <summary>Pure audit decision over (host profile, selected variant, enumerated devices) — unit-testable without I/O.</summary>
    /// <param name="isWindows">Host OS for the operator-facing fallback text; defaults to the running OS, passed explicitly by tests.</param>
    internal static RuntimeDeviceAuditState BuildState(HardwareProfile raw, GpuVariant variant, LlamaDeviceInventory inventory, bool? isWindows = null)
    {
        // A usable GPU is advertised: a vendor GPU with a known, positive total VRAM.
        var gpuExpected = raw.GpuVendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel && (raw.VramBytes ?? 0) > 0;

        // CPU fallback = GPU expected AND the runtime runs on the CPU: a CPU variant, or a GPU variant that RAN and saw
        // zero devices. An indeterminate probe (ProbeSucceeded == false) is "unknown" and never raises a false alarm.
        var cpuVariant = variant == GpuVariant.Cpu;
        var zeroDevices = !cpuVariant && inventory.ProbeSucceeded && inventory.Devices.Count == 0;
        var cpuFallback = gpuExpected && (cpuVariant || zeroDevices);

        var fallbackText = cpuFallback ? BuildFallbackText(raw.GpuVendor, variant, cpuVariant, isWindows ?? OperatingSystem.IsWindows()) : null;

        var backend = ResolveInferenceBackend(variant, inventory);
        return new RuntimeDeviceAuditState
        {
            InferenceBackend = backend,
            GpuExpected = gpuExpected,
            CpuFallback = cpuFallback,
            Reason = fallbackText?.Reason,
            Remediation = fallbackText?.Remediation,
            BackendUndeterminedReason = backend == "unknown" ? BuildUndeterminedText(variant, inventory.RuntimeMissing) : null,
            Devices =
            [
                .. inventory.Devices.Select(static device => new RuntimeAuditDevice
                {
                    Name = device.Name,
                    TotalBytes = device.TotalBytes,
                    FreeBytes = device.FreeBytes
                })
            ]
        };
    }

    /// <summary>The operator-facing text for an undetermined backend — the probe neither succeeded nor proved a fallback.</summary>
    /// <remarks>
    ///     Sizing downstream assumes a GPU nobody confirmed is reachable, so the text lists possible causes and asserts none. The
    ///     <c>XE_LLAMACPP_SERVER_PATH</c> override is named first because it is the most reachable (measured on Windows 11): pointed at a
    ///     GPU-variant binary that enumerates no devices, it is refused by <c>LlamaCppBinaryManager</c>'s no-silent-CPU invariant, and that
    ///     deliberate refusal arrives here as an exception no probe can tell from a glitch. <paramref name="runtimeMissing" /> is the one
    ///     known cause: no runtime is installed, and a page-load diagnostic must not download hundreds of megabytes to find out.
    /// </remarks>
    private static string BuildUndeterminedText(GpuVariant variant, bool runtimeMissing)
    {
        if (runtimeMissing)
        {
            return $"No llama.cpp runtime is installed yet, so the {VariantName(variant)} GPU devices have not been "
                   + "listed and whether inference will use the GPU is unknown. Model sizing on this page still assumes "
                   + "the GPU's VRAM is usable. The runtime is downloaded when you install it from Node Settings, or the "
                   + "first time you start a chat, benchmark or training run; this page never downloads it by itself.";
        }

        return $"The {VariantName(variant)} llama.cpp runtime is selected, but its GPU devices could not be listed, so "
               + "whether inference will use the GPU is unknown. Model sizing on this page still assumes the GPU's VRAM "
               + "is usable. Common causes: a bring-your-own XE_LLAMACPP_SERVER_PATH override that was rejected because "
               + "it exposes no GPU device for its configured variant (check the log for the override's own error), a "
               + "runtime whose libraries could not be loaded, or a busy GPU driver that made the probe overrun. "
               + "Refreshing the hardware profile re-runs the probe.";
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

    // Windows ships a prebuilt CUDA runtime and has no Vulkan-ICD/WSL2 story, so the Linux wording sends a Windows
    // operator to fix something that does not apply; both branches keep the same reason codes.
    private static FallbackText BuildFallbackText(GpuVendor vendor, GpuVariant variant, bool cpuVariant, bool isWindows)
    {
        const string LinuxRemediation =
            "To run on the GPU: build the CUDA runtime from source with the in-app build feature, or set XE_LLAMACPP_SERVER_PATH + "
            + "XE_LLAMACPP_VARIANT to a GPU-capable llama-server binary. On Linux there is no prebuilt CUDA llama.cpp — the default "
            + "NVIDIA build is Vulkan, which needs a Vulkan ICD; if a bring-your-own override is set, verify XE_LLAMACPP_SERVER_PATH "
            + "points to a valid GPU-capable binary.";

        const string WindowsRemediation =
            "Update the GPU driver, check the GPU is visible to the system, then reinstall the runtime from Node Settings. If a "
            + "bring-your-own override is set, verify XE_LLAMACPP_SERVER_PATH points to a valid GPU-capable binary.";

        if (cpuVariant)
        {
            return new FallbackText
            {
                Reason = $"A {VendorName(vendor)} GPU was detected, but the CPU llama.cpp runtime is selected — inference is running on the CPU.",
                Remediation = isWindows ? WindowsRemediation : LinuxRemediation
            };
        }

        var reason = isWindows
            ? $"The {VariantName(variant)} llama.cpp runtime is selected but enumerated no GPU devices, so inference is silently running on the CPU."
            : $"The {VariantName(variant)} llama.cpp runtime is selected but enumerated no GPU devices (commonly a missing Vulkan ICD under WSL2), so inference is silently running on the CPU.";

        return new FallbackText
        {
            Reason = reason,
            Remediation = isWindows ? WindowsRemediation : LinuxRemediation
        };
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

    // One audit pass: the decided state, the fallback reason code when it fell back, whether the pass is safe to
    // memoize, and the managed-CUDA signal stamp the pass is valid for.
    private sealed record DeviceAuditComputation(RuntimeDeviceAuditState State, string? FallbackReasonCode, bool Determinate, long SignalVersion);

    // Operator-facing prose for a detected CPU fallback: what happened, and what to do about it.
    private sealed record FallbackText
    {
        public required string Reason { get; init; }

        public required string? Remediation { get; init; }
    }
}
