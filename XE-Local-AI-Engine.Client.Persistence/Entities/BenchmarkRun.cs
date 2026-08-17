namespace XE_Local_AI_Engine.Client.Persistence.Entities;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

internal sealed record class BenchmarkRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_runtime_snapshot_json</c>.
    /// </summary>
    public byte[] RuntimeSnapshotJson { get; set; } = [];

    public string PrimaryModelName { get; set; } = string.Empty;
    public LocalModelOrigin? PrimaryModelOrigin { get; set; }
    public string ModelContentFingerprint { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public long AgentVersion { get; set; }
    public int RequestedContextTokens { get; set; }
    public BenchmarkPrimaryStatus PrimaryStatus { get; set; }
    public int? EffectiveContextTokens { get; set; }
    public long? DurationMs { get; set; }
    public int? TotalTokens { get; set; }
    /// <summary>
    ///     Decode throughput (tg) when the runtime reported <see cref="GenerationTokens" />/<see cref="GenerationMs" />,
    ///     otherwise the legacy blended <c>total_tokens / duration_ms</c>. Kept under its original name and column so
    ///     every existing reader is unaffected.
    /// </summary>
    public double? TokensPerSecond { get; set; }

    /// <summary>
    ///     The separated throughput measurement: time to first token (client-side wall clock), and the prompt-processing
    ///     (pp) versus generation (tg) split of tokens and milliseconds as the runtime itself timed them. All null for
    ///     runs frozen before these columns existed and for any runtime that reports no per-request timings — never
    ///     inferred from the blended numbers. Display only: throughput is not a ranking input.
    /// </summary>
    public double? TtftMs { get; set; }

    public int? PromptTokens { get; set; }
    public double? PromptMs { get; set; }
    public int? GenerationTokens { get; set; }
    public double? GenerationMs { get; set; }
    public int? CachedPromptTokens { get; set; }

    /// <summary>How many provider requests the turn made; above 1 means the sums above span a tool-calling loop.</summary>
    public int? SegmentCount { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_output_parts_json</c>.
    /// </summary>
    public byte[]? OutputPartsJson { get; set; }

    public long LastStreamSequence { get; set; }
    public int? UserScore { get; set; }

    /// <summary>
    ///     The repeat group this run belongs to, or <see langword="null" /> for a single run. Every run a batch of
    ///     repeats created shares one id, so a reader can tell "three measurements of one launch" from "three
    ///     unrelated runs that happen to name the same model".
    /// </summary>
    public Guid? RepeatGroupId { get; set; }

    /// <summary>
    ///     Position inside <see cref="RepeatGroupId" />: <c>0</c> is the warm-up run (only when one was requested),
    ///     and the measured repeats are <c>1..N</c>. Null exactly when <see cref="RepeatGroupId" /> is null.
    /// </summary>
    public int? RepeatIndex { get; set; }

    /// <summary>
    ///     A warm-up run: measured and stored like any other, but never ranked and never counted in a group's
    ///     statistics. Its whole purpose is to absorb the first-launch costs the runs after it should not pay for.
    /// </summary>
    public bool IsWarmup { get; set; }

    /// <summary>The judge attempt whose verdict this run currently shows. Null until the first attempt is enqueued.</summary>
    public Guid? CurrentJudgeAttemptId { get; set; }

    /// <summary>
    ///     What freeze INTENDED this run to launch, per phase. All null for rows created before launch evidence
    ///     existed (they are displayed as "—", never inferred).
    /// </summary>
    public string? PrimaryVariant { get; set; }

    public string? PrimaryKvCacheType { get; set; }
    public string? PrimaryKvCacheTypeSource { get; set; }
    public string? PrimaryKvAutoReason { get; set; }
    public string? PrimaryFlashAttentionMode { get; set; }
    public string? PrimaryIntendedLaunchIdentity { get; set; }
    public string? PrimaryIntendedExecutableSha256 { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_primary_launch_receipt_json</c>. Written once, before inference, and never overwritten.
    /// </summary>
    public byte[]? PrimaryLaunchReceiptJson { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_primary_environment_facts_json</c>.
    /// </summary>
    public byte[]? PrimaryEnvironmentFactsJson { get; set; }

    public string? PrimaryReceiptHash { get; set; }
    public string? PrimaryEnvironmentFactsHash { get; set; }
    public string? PrimaryEffectiveLaunchIdentity { get; set; }
    public string? PrimaryEffectiveBackend { get; set; }
    public int? PrimaryPlacementOffloaded { get; set; }
    public int? PrimaryPlacementTotal { get; set; }
    public string? PrimaryLaunchExecutableSha256 { get; set; }
    public bool? PrimaryLaunchHasAuxAssets { get; set; }
    public string? PrimaryLaunchKvCacheTypeSource { get; set; }

    /// <summary>
    ///     Why the primary generation stopped, verbatim from the provider (<c>stop</c>, <c>length</c>,
    ///     <c>tool_calls</c>, <c>content_filter</c>). Plaintext, not sensitive. Null on runs frozen before this column
    ///     existed and on any run whose provider reported no finish reason — never inferred from the status.
    /// </summary>
    public string? PrimaryStopReason { get; set; }

    public string? PrimaryErrorMessage { get; set; }
    public long Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? PrimaryCompletedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
