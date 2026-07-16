namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     The node runtime device audit (AUD4-03): compares the host hardware profile against the devices the SELECTED
///     llama.cpp runtime actually enumerates, to detect a silent CPU fallback (a GPU box whose inference runs on the
///     CPU — a CPU variant selected, or a GPU variant that sees zero devices, e.g. the Vulkan build under WSL2 with no
///     ICD). The audit is a pure function of the selected binary, so it is computed on first demand and cached; that is
///     the cheapest correct point — recomputing per spawn would add cost for no new information, and it adds ZERO
///     latency to a warm (reused) inference path.
/// </summary>
public interface IRuntimeDeviceAudit
{
    /// <summary>
    ///     Returns the current audit state, recomputing when <paramref name="forceRefresh" /> is set. On a fresh
    ///     detection of a CPU fallback (state change) it logs a structured warning and increments the device-fallback
    ///     counter — once per binary, not per call.
    /// </summary>
    Task<RuntimeDeviceAuditState> GetAuditAsync(bool forceRefresh, CancellationToken ct);

    /// <summary>
    ///     The EFFECTIVE hardware profile the advisor + capacity gate must size against: the raw profile (its live free
    ///     figures re-probed when <paramref name="forceRefreshProfile" /> is set) degraded to CPU-mode (VRAM unknown)
    ///     when the audit reports a CPU fallback, so model sizing never pretends VRAM exists that the runtime cannot use.
    ///     When the GPU is actually working the effective profile is the raw profile unchanged.
    /// </summary>
    Task<HardwareProfile> GetEffectiveProfileAsync(bool forceRefreshProfile, CancellationToken ct);
}
