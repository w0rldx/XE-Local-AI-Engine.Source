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
    public double? TokensPerSecond { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_output_parts_json</c>.
    /// </summary>
    public byte[]? OutputPartsJson { get; set; }

    public long LastStreamSequence { get; set; }
    public int? UserScore { get; set; }
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

    public string? PrimaryErrorMessage { get; set; }
    public long Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? PrimaryCompletedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
