namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Judges a persisted, frozen inference profile against the freeze baseline: still replayable, STALE because the
///     runtime build, launch policy, hardware or placement drifted, or only under transient free-VRAM pressure.
/// </summary>
/// <remarks>A PURE verdict: it never evicts, kills or restarts a running process (invariant: stale &#8800; evict).</remarks>
public interface IInferenceInvalidationEvaluator
{
    /// <summary>
    ///     Returns <see cref="InferenceProfileValidity.Stale" /> when a lasting axis drifted,
    ///     <see cref="InferenceProfileValidity.UnderPressure" /> when only live free VRAM dropped below the baseline.
    /// </summary>
    /// <remarks>
    ///     Build, launch-policy and hardware drift win over pressure; placement is judged only without pressure, since
    ///     its verdict is cached and must not be priced against pressured free VRAM. Degrades safely, skipping a check
    ///     rather than reporting stale, when an input is unknown: an unreadable free VRAM never reports pressure, while a
    ///     timed-out GPU probe reports pressure for a GPU row, so it can neither demote nor replay one.
    /// </remarks>
    Task<InferenceProfileValidity> EvaluateAsync(InferenceProfileRecord profile, CancellationToken ct);

    /// <summary>
    ///     Returns <see langword="true" /> when replaying <paramref name="profile" /> would CONTRADICT today's
    ///     placement verdict: the current memory-fit estimate puts the model's expert weights in system RAM, but the
    ///     row carries no tensor override.
    /// </summary>
    /// <remarks>
    ///     Such a replay would launch a Mixture-of-Experts model fully resident and oversubscribe the device.
    ///     <c>-ot</c> IS the frozen expert placement, so a row that has one cannot hide the decision, and a
    ///     resident/dense verdict never trips this axis. Degrades safely, reporting no contradiction, when the verdict
    ///     is unavailable.
    /// </remarks>
    Task<bool> ContradictsCurrentPlacementAsync(InferenceProfileRecord profile, CancellationToken ct);
}
