namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
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
    UnsupportedSnapshot
}

public enum BenchmarkCancelTarget
{
    Primary,
    Judge
}

public sealed class BenchmarkErrorResponse
{
    public required BenchmarkErrorCode Code { get; init; }
    public required string Message { get; init; }
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
    public string? JudgeModelName { get; init; }
    public int? JudgeContextTokens { get; init; }
    public int JudgePromptVersion { get; init; }
    public int JudgeOutputSchemaVersion { get; init; }
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
    public BenchmarkCancelTarget Target { get; init; }
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
    public BenchmarkJudgeStatus JudgeStatus { get; init; }
    public int? EffectiveContextTokens { get; init; }
    public long? DurationMs { get; init; }
    public int? TotalTokens { get; init; }
    public double? TokensPerSecond { get; init; }
    public int? UserScore { get; init; }
    public long LastStreamSequence { get; init; }
    public long Version { get; init; }
    public long CreatedAtUtc { get; init; }
    public long UpdatedAtUtc { get; init; }
}

public sealed class BenchmarkRunDetailResponse : BenchmarkRunSummaryResponse
{
    public JsonElement? OutputParts { get; init; }
    public BenchmarkJudgeResultV1? JudgeResult { get; init; }
    public string? PrimaryErrorMessage { get; init; }
    public string? JudgeErrorMessage { get; init; }
    public long? StartedAtUtc { get; init; }
    public long? PrimaryCompletedAtUtc { get; init; }
    public long? JudgeStartedAtUtc { get; init; }
    public long? JudgeCompletedAtUtc { get; init; }
}

public sealed class BenchmarkJudgeResultV1
{
    public int SchemaVersion { get; init; } = 1;
    public int Score { get; init; }
    public required string Rationale { get; init; }
    public required string JudgeModelContentFingerprint { get; init; }
    public int PromptVersion { get; init; }
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
