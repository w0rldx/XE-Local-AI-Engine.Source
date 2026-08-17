namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     A single GPU device the SELECTED llama.cpp runtime enumerated (name + total / free VRAM in bytes where the build
///     prints them). Surfaced on the node runtime-audit state so the operator can see what the runtime actually found.
/// </summary>
public sealed record RuntimeAuditDevice(string Name, long? TotalBytes, long? FreeBytes);

/// <summary>
///     The node-level runtime device audit: whether the SELECTED inference runtime is actually using the GPU
///     the host advertises, or has silently fallen back to the CPU. Surfaced over REST for the operator UI and consumed
///     by the capacity gate + model advisor (via the audited effective hardware profile) so model sizing matches the
///     runtime that will actually run.
/// </summary>
public sealed record RuntimeDeviceAuditState
{
    /// <summary>The backend inference actually uses: <c>cuda</c> | <c>vulkan</c> | <c>cpu</c> | <c>unknown</c>.</summary>
    public required string InferenceBackend { get; init; }

    /// <summary><see langword="true" /> when the host advertises a usable GPU (a vendor GPU with known VRAM &gt; 0).</summary>
    public required bool GpuExpected { get; init; }

    /// <summary>
    ///     <see langword="true" /> when a GPU is expected but the selected runtime runs on the CPU — a CPU variant chosen
    ///     on a GPU box, or a GPU variant that enumerated zero devices. Never set from an indeterminate probe (no false alarm).
    /// </summary>
    public required bool CpuFallback { get; init; }

    /// <summary>Operator-facing explanation of the fallback (likely cause), or <see langword="null" /> when not in fallback.</summary>
    public string? Reason { get; init; }

    /// <summary>Operator-facing remediation (in-app paths) for the fallback, or <see langword="null" /> when not in fallback.</summary>
    public string? Remediation { get; init; }

    /// <summary>The GPU devices the selected runtime enumerated (empty when none, or when the probe was indeterminate).</summary>
    public IReadOnlyList<RuntimeAuditDevice> Devices { get; init; } = [];

    /// <summary>
    ///     Operator-facing explanation for an INDETERMINATE audit (<see cref="InferenceBackend" /> is <c>unknown</c>
    ///     because the device probe timed out or could not spawn), or <see langword="null" /> when the backend is
    ///     known. Deliberately separate from <see cref="Reason" />: "we could not tell" is not a CPU fallback, and
    ///     folding it into <see cref="CpuFallback" /> would raise a false alarm about a machine that may be perfectly
    ///     healthy. Without this, a wedged driver is indistinguishable from health — the UI shows nothing at all.
    /// </summary>
    public string? BackendUndeterminedReason { get; init; }

    /// <summary>
    ///     What the most recent observed model load actually did with that model's layers, or <see langword="null" />
    ///     when no load has been observed. Distinct from <see cref="CpuFallback" />: a partial offload means the GPU IS
    ///     in use, just not for the whole model, so conflating the two would make the fallback banner lie.
    /// </summary>
    /// <remarks>
    ///     Unlike the rest of this record — which is memoized per binary — this field is re-stamped from the live
    ///     placement report on every read, because it changes as models load rather than when the binary changes.
    /// </remarks>
    public LlamaLayerPlacement? LayerPlacement { get; init; }
}
