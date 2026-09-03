namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Decides whether a persisted, frozen inference profile is STALE — i.e. the box's runtime build or hardware has
///     drifted from the freeze baseline so the frozen launch args can no longer be trusted and a fresh explore is
///     required. A PURE verdict: it never evicts, kills, or restarts a running process (invariant: stale &#8800; evict).
/// </summary>
public interface IInferenceInvalidationEvaluator
{
    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="profile" /> should be re-explored: the active llama.cpp
    ///     build differs from the frozen build, the GPU vendor/VRAM has materially changed, or live free VRAM has dropped
    ///     below the frozen baseline. Degrades safely (skips a check rather than reporting stale) when an input is unknown.
    /// </summary>
    Task<bool> IsStaleAsync(InferenceProfileRecord profile, CancellationToken ct);

    /// <summary>
    ///     Returns <see langword="true" /> when replaying <paramref name="profile" /> would CONTRADICT today's placement
    ///     verdict: the current memory-fit estimate puts the model's expert weights in system RAM, but the row carries no
    ///     tensor override, so its replay would launch a Mixture-of-Experts model fully resident and oversubscribe the
    ///     device. <c>-ot</c> IS the frozen expert placement, so a row that has one cannot hide the decision, and a
    ///     resident/dense verdict never trips this axis. Degrades safely (reports no contradiction) when the verdict is
    ///     unavailable, leaving the caller exactly where it stood before this check existed.
    /// </summary>
    Task<bool> ContradictsCurrentPlacementAsync(InferenceProfileRecord profile, CancellationToken ct);
}
