namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

public enum BenchmarkErrorCode
{
    InvalidRequest,
    NotFound,
    VersionConflict,
    ProjectFrozen,
    ActiveRun,
    InvalidLifecycleTransition,
    FreezeDependencyChanged,
    FingerprintChanged,
    IneligibleAgent,
    IneligibleModel,
    UnsupportedSnapshot,
    UnsupportedKvCacheType,
    RejudgeRequired,
    JudgeAttemptsActive,
    JudgeAttemptActive,
    JudgePolicyAlreadyApplied,
    JudgePolicyChanged,
    JudgeDisabled,
    PrimaryNotSucceeded,

    /// <summary>Batch only: the cell never reached the freeze because an earlier cell stopped the batch.</summary>
    NotAttempted,

    /// <summary>
    ///     Batch only: the request's time budget ran out before this cell was frozen. Nothing is wrong with the cell —
    ///     resubmit it, with the project version the response reports.
    /// </summary>
    BatchTimeBudget
}
