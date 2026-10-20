namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

/// <summary>One rubric criterion's reviewer score, 0..10.</summary>
/// <remarks><see cref="Score" /> is nullable so an omitted score is a 400, never a silent 0.</remarks>
public sealed class BenchmarkReviewCriterionScoreDto
{
    public string CriterionId { get; init; } = string.Empty;
    public int? Score { get; init; }
}

/// <summary>A reviewer's decision on one scoring run of a review project.</summary>
/// <remarks>
///     <see cref="State" /> is <c>approved</c> or <c>rejected</c>. Under a judge policy an approval scores every <c>llm</c> criterion and the overall is
///     computed from the rubric weights; without one, <see cref="Score" /> is the 0..100 overall.
/// </remarks>
public sealed class SubmitBenchmarkRunReviewRequest
{
    public Guid RunId { get; init; }
    public string State { get; init; } = string.Empty;
    public IReadOnlyList<BenchmarkReviewCriterionScoreDto>? Criteria { get; init; }
    public int? Score { get; init; }
    public string? Notes { get; init; }
    public long ExpectedVersion { get; init; }
}

public sealed class BenchmarkRunReviewResponse
{
    /// <summary><c>approved</c> or <c>rejected</c>.</summary>
    public required string State { get; init; }

    public int? Score { get; init; }
    public IReadOnlyList<BenchmarkReviewCriterionScoreDto> Criteria { get; init; } = [];
    public string? Notes { get; init; }
    public Guid? PolicyRevisionId { get; init; }

    /// <summary>False when the decision was made under a judge-policy revision the project no longer judges under: the run is back in the inbox.</summary>
    public bool PolicyCurrent { get; init; }

    public long UpdatedAtUtc { get; init; }
}

/// <summary>Metadata of one immutable run artifact; its bytes are downloaded from their own route and never exported.</summary>
public sealed class BenchmarkRunArtifactResponse
{
    public Guid Id { get; init; }

    /// <summary><c>pythonTestsStdout</c> or <c>pythonTestsStderr</c>.</summary>
    public required string Kind { get; init; }

    public string? CriterionId { get; init; }
    public Guid JudgeAttemptId { get; init; }
    public long ByteCount { get; init; }
    public required string ContentHash { get; init; }
}

public sealed class BenchmarkRunArtifactRouteRequest
{
    public Guid RunId { get; init; }
    public Guid ArtifactId { get; init; }
}

public sealed class ListBenchmarkReviewsRequest
{
    /// <summary><c>pending</c> (the default), <c>approved</c>, <c>rejected</c> or <c>stale</c>.</summary>
    public string State { get; init; } = "pending";

    public Guid? ProjectId { get; init; }
    public int Page { get; init; } = 1;
    public int Take { get; init; } = 50;
}

/// <summary>One inbox row, from flat columns only.</summary>
public sealed class BenchmarkReviewQueueItemResponse
{
    public Guid RunId { get; init; }
    public Guid ProjectId { get; init; }

    /// <summary>The run version a review of this row must send as <c>expectedVersion</c>.</summary>
    public long RunVersion { get; init; }

    public required string PrimaryModelName { get; init; }
    public Guid? TaskItemId { get; init; }
    public int? TaskItemIndex { get; init; }
    public long? PrimaryCompletedAtUtc { get; init; }

    /// <summary>The row's queue state: <c>pending</c>, <c>approved</c>, <c>rejected</c> or <c>stale</c>.</summary>
    public required string State { get; init; }

    public int? ReviewScore { get; init; }
    public long? ReviewedAtUtc { get; init; }
}

public sealed class ListBenchmarkReviewsResponse
{
    public IReadOnlyList<BenchmarkReviewQueueItemResponse> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int Take { get; init; }
}
