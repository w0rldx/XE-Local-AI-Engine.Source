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
///     The frozen capacity-gate contract. <see cref="Verdict" /> drives the spawn dispatch; <see cref="Reason" /> is a
///     sanitized, user-safe constant (no paths/secrets/tokens) handed back to the calling agent on a reject;
///     <see cref="OllamaEvictionWarning" /> flags that admitting/serializing this spawn on the best-effort Ollama
///     provider may evict a different running model. On an <see cref="CapacityVerdict.Allow" /> for a LOCAL model the
///     decision also carries a <see cref="Reservation" /> that owns both the exact llama.cpp launch admission and this
///     model's pending-footprint ledger reservation — the caller MUST dispose it when the spawned child exits.
///     For cloud Allow, QueueSameModel and every reject, <see cref="Reservation" /> is <see langword="null" /> (nothing
///     to release).
/// </summary>
/// <param name="Verdict">The admission verdict.</param>
/// <param name="Reason">Sanitized, user-safe reason string (constant; never a path/secret).</param>
/// <param name="OllamaEvictionWarning">Whether loading this model on Ollama may evict a different running model.</param>
/// <param name="Reservation">
///     The composite launch-admission/ledger reservation to release on child exit (local Allow only);
///     <see langword="null" /> otherwise. Disposing it is idempotent.
/// </param>
public sealed record CapacityDecision(
    CapacityVerdict Verdict,
    string Reason,
    bool OllamaEvictionWarning,
    IDisposable? Reservation = null);
