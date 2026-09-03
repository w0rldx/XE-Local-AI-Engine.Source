namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One judging of one run under one policy revision. Immutable evidence: a re-judge inserts a new attempt rather
///     than overwriting the previous one, so a run's judging history survives policy and runtime changes.
/// </summary>
internal sealed record class BenchmarkJudgeAttempt
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>1..n within the run, in enqueue order.</summary>
    public int Sequence { get; set; }

    public Guid PolicyRevisionId { get; set; }

    /// <summary>Copied from the revision at enqueue; gates rank membership rather than only promotion.</summary>
    public int CohortGeneration { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_judge_runtime_json</c>. NULL for an attempt inserted directly as
    ///     <see cref="BenchmarkJudgeAttemptStatus.Failed" /> before the judge runtime could be resolved.
    /// </summary>
    public byte[]? JudgeRuntimeJson { get; set; }

    /// <summary>
    ///     The rank-cohort key of this execution, written at the judge launch-ready boundary. NULL when the launch
    ///     never reached readiness or the execution identity was incomplete — such an attempt is never ranked.
    /// </summary>
    public string? JudgeExecutionKey { get; set; }

    public BenchmarkJudgeAttemptStatus Status { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_judge_attempt_result_json</c>.
    /// </summary>
    public byte[]? ResultJson { get; set; }

    /// <summary>The server-computed 0..100 quality score. Plaintext so ranking is a SQL sort, not a decrypt.</summary>
    public int? Score { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_judge_attempt_launch_receipt_json</c>. Written once, before inference, never overwritten.
    /// </summary>
    public byte[]? LaunchReceiptJson { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_judge_attempt_environment_facts_json</c>.
    /// </summary>
    public byte[]? EnvironmentFactsJson { get; set; }

    /// <summary>What freeze INTENDED this attempt to launch with, resolved at enqueue.</summary>
    public string? Variant { get; set; }

    public string? KvCacheType { get; set; }
    public string? KvCacheTypeSource { get; set; }
    public string? KvAutoReason { get; set; }
    public string? FlashAttentionMode { get; set; }
    public string? IntendedLaunchIdentity { get; set; }
    public string? IntendedExecutableSha256 { get; set; }

    /// <summary>
    ///     The launch-identity SCHEME <see cref="IntendedLaunchIdentity" /> was computed under. NULL on a row frozen
    ///     before the scheme was recorded, which reads as scheme 1.
    /// </summary>
    public int? LaunchIdentityScheme { get; set; }
    public string? ReceiptHash { get; set; }
    public string? EnvironmentFactsHash { get; set; }
    public string? EffectiveLaunchIdentity { get; set; }
    public string? EffectiveBackend { get; set; }
    public int? PlacementOffloaded { get; set; }
    public int? PlacementTotal { get; set; }
    public string? LaunchExecutableSha256 { get; set; }
    public bool? LaunchHasAuxAssets { get; set; }
    public string? LaunchKvCacheTypeSource { get; set; }
    public long EnqueuedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? CompletedAtUtc { get; set; }
    public long Version { get; set; }
}
