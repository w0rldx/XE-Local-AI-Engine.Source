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
    public BenchmarkJudgeStatus JudgeStatus { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_judge_result_json</c>.
    /// </summary>
    public byte[]? JudgeResultJson { get; set; }

    public string? PrimaryErrorMessage { get; set; }
    public string? JudgeErrorMessage { get; set; }
    public long Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long? StartedAtUtc { get; set; }
    public long? PrimaryCompletedAtUtc { get; set; }
    public long? JudgeStartedAtUtc { get; set; }
    public long? JudgeCompletedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
