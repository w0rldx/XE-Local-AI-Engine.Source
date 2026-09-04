namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One pairwise judging of two runs against each other, in one presentation order. The pair itself is unordered —
///     <see cref="RunAId" /> is always the smaller GUID — and <see cref="Order" /> records which side the judge saw
///     first, so position bias is measured rather than assumed away. Immutable in exactly the sense
///     <see cref="BenchmarkJudgeAttempt" /> is: a retry inserts a row at the next <see cref="AttemptSequence" />.
/// </summary>
internal sealed record class BenchmarkJudgeComparison
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PolicyRevisionId { get; set; }

    /// <summary>Copied from the revision at enqueue; a verdict never crosses a cohort.</summary>
    public int CohortGeneration { get; set; }

    /// <summary>The task case both runs answered. Null for a legacy single-case comparison; otherwise the leaf task item.</summary>
    public Guid? TaskCaseId { get; set; }

    /// <summary>The hash of that case's input, empty for a legacy single-case comparison. Different hashes never pair.</summary>
    public string TaskInputHash { get; set; } = string.Empty;

    /// <summary>The canonically smaller of the two run ids by GUID ordinal — enforced by a CHECK, not only here.</summary>
    public Guid RunAId { get; set; }

    public Guid RunBId { get; set; }

    /// <summary><c>0</c> = A shown first, <c>1</c> = B shown first. Both orders of every pair are required.</summary>
    public int Order { get; set; }

    /// <summary>1..n for this slot: a re-enqueue after a failure or cancellation increments it.</summary>
    public int AttemptSequence { get; set; }

    /// <summary>1..n within the cohort, in enqueue order.</summary>
    public int Sequence { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_comparison_runtime_json</c>.
    /// </summary>
    public byte[]? JudgeRuntimeJson { get; set; }

    /// <summary>The rank-cohort key of this execution, written at the launch-ready boundary.</summary>
    public string? JudgeExecutionKey { get; set; }

    public BenchmarkJudgeAttemptStatus Status { get; set; }

    /// <summary>
    ///     <c>a</c>, <c>b</c> or <c>tie</c>, normalized back to the canonical pair regardless of presentation order.
    ///     Plaintext because it is the rankable signal — the same posture as
    ///     <see cref="BenchmarkJudgeAttempt.Score" />.
    /// </summary>
    public string? Verdict { get; set; }

    /// <summary>Whether each side had to be truncated to fit its half of the judge window.</summary>
    public bool AnswerATruncated { get; set; }

    public bool AnswerBTruncated { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_comparison_result_json</c>. The judge's rationale.
    /// </summary>
    public byte[]? ResultJson { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_comparison_launch_receipt_json</c>. Written once, before inference, never overwritten.
    /// </summary>
    public byte[]? LaunchReceiptJson { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_comparison_environment_facts_json</c>.
    /// </summary>
    public byte[]? EnvironmentFactsJson { get; set; }

    /// <summary>What the enqueue INTENDED this comparison to launch with.</summary>
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
