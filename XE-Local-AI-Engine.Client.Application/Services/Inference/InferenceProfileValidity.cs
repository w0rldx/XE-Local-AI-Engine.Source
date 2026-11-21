namespace XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>
///     The verdict <see cref="IInferenceInvalidationEvaluator" /> returns for a frozen inference profile at one cold
///     spawn: replay it, demote it for good, or bypass it for this launch only.
/// </summary>
public enum InferenceProfileValidity
{
    /// <summary>Every axis holds: the frozen launch arguments may be replayed.</summary>
    Valid,

    /// <summary>A lasting axis drifted (build, launch policy, hardware or placement): the profile must be re-explored.</summary>
    Stale,

    /// <summary>
    ///     Live global free VRAM sits materially below the freeze baseline: this launch auto-fits, and the profile
    ///     stays frozen for the next spawn. Placement is not judged under pressure.
    /// </summary>
    UnderPressure
}
