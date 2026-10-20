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

    /// <summary>The project requires review: its user score is written by the review endpoint only.</summary>
    ReviewRequired,

    /// <summary>The run is not a reviewable scoring run, or its current judging has not succeeded under the current policy.</summary>
    NotReviewable,

    /// <summary>The artifact's row exists but its bytes are gone.</summary>
    ArtifactMissing,

    /// <summary>The artifact's bytes failed decryption or no longer match the recorded hash or size; they are never served.</summary>
    ArtifactCorrupt,

    /// <summary>Batch only: the cell never reached the freeze because an earlier cell stopped the batch.</summary>
    NotAttempted,

    /// <summary>
    ///     Batch only: the request's time budget ran out before this cell was frozen. Nothing is wrong with the cell —
    ///     resubmit it, with the project version the response reports.
    /// </summary>
    BatchTimeBudget
}
