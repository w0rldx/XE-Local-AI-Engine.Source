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
}
