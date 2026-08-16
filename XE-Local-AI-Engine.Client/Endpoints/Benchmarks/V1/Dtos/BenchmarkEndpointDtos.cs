namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

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
    UnsupportedKvCacheType
}

public class BenchmarkProjectMutationRequest
{
    public string Name { get; init; } = string.Empty;
    public string CoreTask { get; init; } = string.Empty;
    public int ContextTokens { get; init; }
    public Guid AgentDefinitionId { get; init; }
    public bool JudgeEnabled { get; init; }
    public string? JudgeModelName { get; init; }
    public int? JudgeContextTokens { get; init; }
    public int JudgePromptVersion { get; init; } = 1;
    public int JudgeOutputSchemaVersion { get; init; } = 1;
}

public sealed class UpdateBenchmarkProjectRequest : BenchmarkProjectMutationRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class BenchmarkProjectRouteRequest
{
    public Guid ProjectId { get; init; }
}

public sealed class DeleteBenchmarkProjectRequest
{
    public Guid ProjectId { get; init; }
    public long ExpectedVersion { get; init; }
}

public class BenchmarkProjectSummaryResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public int ContextTokens { get; init; }
    public Guid AgentDefinitionId { get; init; }
    public bool JudgeEnabled { get; init; }
    public int RunCount { get; init; }
    public bool IsFrozen { get; init; }
    public long Version { get; init; }
    public long CreatedAtUtc { get; init; }
    public long UpdatedAtUtc { get; init; }
}

public sealed class BenchmarkProjectDetailResponse : BenchmarkProjectSummaryResponse
{
    public required string CoreTask { get; init; }
    /// <summary>The policy revision the project judges under, or null when judging is off. S2 projects the policy itself.</summary>
    public Guid? CurrentJudgePolicyRevisionId { get; init; }
}

public sealed class ListBenchmarkProjectsResponse
{
    public IReadOnlyList<BenchmarkProjectSummaryResponse> Items { get; init; } = [];
}

public sealed class ListBenchmarkRunsRequest
{
    public Guid ProjectId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

public sealed class StartBenchmarkRunRequest
{
    public Guid ProjectId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public long ExpectedProjectVersion { get; init; }

    /// <summary><c>f16</c>, <c>q8_0</c>, <c>q4_0</c>, or <see langword="null" /> for Auto (the node picks).</summary>
    public string? KvCacheType { get; init; }
}

public sealed class BenchmarkRunRouteRequest
{
    public Guid RunId { get; init; }
}

public sealed class DeleteBenchmarkRunRequest
{
    public Guid RunId { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class CancelBenchmarkRunRequest
{
    public Guid RunId { get; init; }
    public BenchmarkCancellationTarget Target { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class ScoreBenchmarkRunRequest
{
    public Guid RunId { get; init; }
    public int Score { get; init; }
    public long ExpectedVersion { get; init; }
}

public class BenchmarkRunSummaryResponse
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public required string PrimaryModelName { get; init; }
    public LocalModelOrigin? PrimaryModelOrigin { get; init; }
    public required string ModelContentFingerprint { get; init; }
    public required string AgentName { get; init; }
    public long AgentVersion { get; init; }
    public int RequestedContextTokens { get; init; }
    public BenchmarkPrimaryStatus PrimaryStatus { get; init; }
    /// <summary>
    ///     The derived judge state of the run's current attempt: <c>none|queued|running|succeeded|failed|cancelled</c>.
    /// </summary>
    public required string JudgeStatus { get; init; }

    /// <summary>The current attempt's 0..100 score, or null when it has none.</summary>
    public int? JudgeScore { get; init; }

    /// <summary>Why this run is not in the project's ranked cohort, or null when it is ranked.</summary>
    public string? RankExclusionReason { get; init; }
    public int? EffectiveContextTokens { get; init; }
    public long? DurationMs { get; init; }
    public int? TotalTokens { get; init; }
    public double? TokensPerSecond { get; init; }
    public int? UserScore { get; init; }
    public long LastStreamSequence { get; init; }
    public long Version { get; init; }
    public long CreatedAtUtc { get; init; }
    public long UpdatedAtUtc { get; init; }

    /// <summary>What freeze intended this run to launch. All null for runs frozen before launch evidence existed.</summary>
    public string? PrimaryVariant { get; set; }

    public string? PrimaryKvCacheType { get; set; }
    public string? PrimaryKvCacheTypeSource { get; set; }
    public string? PrimaryKvAutoReason { get; set; }
    public string? PrimaryFlashAttentionMode { get; set; }
    public string? PrimaryIntendedLaunchIdentity { get; set; }
    public string? PrimaryIntendedExecutableSha256 { get; set; }

    /// <summary>What the launch itself recorded. All null until the spawn reached readiness.</summary>
    public string? PrimaryEffectiveLaunchIdentity { get; set; }

    public string? PrimaryEffectiveBackend { get; set; }
    public int? PrimaryPlacementOffloaded { get; set; }
    public int? PrimaryPlacementTotal { get; set; }
    public string? PrimaryExecutableSha256 { get; set; }
    public bool? PrimaryHasAuxAssets { get; set; }
    public string? PrimaryReceiptHash { get; set; }
    public string? PrimaryEnvironmentFactsHash { get; set; }
}

public sealed class BenchmarkRunDetailResponse : BenchmarkRunSummaryResponse
{
    public JsonElement? OutputParts { get; init; }
    /// <summary>The rubric verdict of the current attempt (<c>BenchmarkJudgeResultV2</c>), or null.</summary>
    public JsonElement? JudgeResult { get; init; }
    public string? PrimaryErrorMessage { get; init; }
    public string? JudgeErrorMessage { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? PrimaryCompletedAtUtc { get; init; }

    /// <summary>The decoded launch receipt (<c>LlamaServerLaunchReceipt</c> v1), or null when none was recorded.</summary>
    public JsonElement? PrimaryLaunchReceipt { get; init; }

    /// <summary>The decoded pre-launch environment capture (<c>RuntimeEnvironmentFactsV1</c>), or null.</summary>
    public JsonElement? PrimaryEnvironmentFacts { get; init; }

}

public sealed class ListBenchmarkRunsResponse
{
    public IReadOnlyList<BenchmarkRunSummaryResponse> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
}

public sealed class EligibleBenchmarkAgentsRequest
{
    public string ModelName { get; init; } = string.Empty;
}

public sealed class EligibleBenchmarkAgentResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public int Version { get; init; }
}

public sealed class ListEligibleBenchmarkAgentsResponse
{
    public IReadOnlyList<EligibleBenchmarkAgentResponse> Items { get; init; } = [];
}

public sealed class EligibleBenchmarkModelsRequest
{
    public int? ContextTokens { get; init; }
}

public sealed class EligibleBenchmarkModelResponse
{
    public required string ModelName { get; init; }
    public int? MaxContextTokens { get; init; }
    public int? EffectiveContextTokens { get; init; }
    public LocalModelOrigin? Origin { get; init; }
    public required string ModelContentFingerprint { get; init; }
    public bool SupportsTools { get; init; }
}

public sealed class ListEligibleBenchmarkModelsResponse
{
    public IReadOnlyList<EligibleBenchmarkModelResponse> Items { get; init; } = [];
}
