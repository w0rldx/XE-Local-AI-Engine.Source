namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     The single admission verdict the capacity gate returns for a sub-agent spawn keyed on its <c>(model, role)</c>.
/// </summary>
public enum CapacityVerdict
{
    /// <summary>The spawn may load/run now (cloud bypass, or a local model that fits the byte budget AND the process cap).</summary>
    Allow = 0,

    /// <summary>The target <c>(model, role)</c> is already running locally; the spawn must serialize on that one process (no second load).</summary>
    QueueSameModel = 1,

    /// <summary>The local model cannot be admitted — it would overcommit the byte budget or the process cap, or its footprint/budget is unknown.</summary>
    RejectInsufficient = 2
}

/// <summary>
///     The frozen capacity-gate contract: the verdict that drives the spawn dispatch, its user-safe reason, and the
///     reservation a local <see cref="CapacityVerdict.Allow" /> hands back.
/// </summary>
/// <remarks>
///     <see cref="Reason" /> is a sanitized constant — never a path, secret or token — handed to the calling agent on a
///     reject; <see cref="OllamaEvictionWarning" /> flags that admitting or serializing this spawn on the best-effort
///     Ollama provider may evict a different running model. A LOCAL Allow carries a <see cref="Reservation" /> owning
///     both the exact llama.cpp launch admission and this model's pending-footprint ledger booking, which the caller
///     MUST dispose on child exit; it is null for cloud Allow, QueueSameModel and every reject.
/// </remarks>
public sealed class CapacityDecision
{
    /// <summary>The admission verdict.</summary>
    public required CapacityVerdict Verdict { get; init; }

    /// <summary>Sanitized, user-safe reason string (constant; never a path/secret).</summary>
    public required string Reason { get; init; }

    /// <summary>Whether loading this model on Ollama may evict a different running model.</summary>
    public required bool OllamaEvictionWarning { get; init; }

    /// <summary>
    ///     The composite launch-admission/ledger reservation to release on child exit (local Allow only);
    ///     <see langword="null" /> otherwise. Disposing it is idempotent.
    /// </summary>
    public IDisposable? Reservation { get; init; }
}
