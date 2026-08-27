namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

public sealed class ListBenchmarkComparisonsRequest
{
    public Guid ProjectId { get; init; }
}

/// <summary>One ordered pairwise judging. The verdict is already normalized back to the canonical pair.</summary>
public sealed class BenchmarkComparisonResponse
{
    public Guid Id { get; init; }
    public Guid RunAId { get; init; }
    public Guid RunBId { get; init; }

    /// <summary><c>0</c> = A shown first, <c>1</c> = B shown first. Both orders exist for every pair.</summary>
    public int Order { get; init; }

    public int AttemptSequence { get; init; }
    public int Sequence { get; init; }
    public Guid? TaskCaseId { get; init; }
    public required string Status { get; init; }
    public string? Verdict { get; init; }
    public bool AnswerATruncated { get; init; }
    public bool AnswerBTruncated { get; init; }
    public string? JudgeExecutionKey { get; init; }
    public string? ErrorMessage { get; init; }
    public long EnqueuedAtUtc { get; init; }
    public long? CompletedAtUtc { get; init; }
}

/// <summary>One run's fitted strength and its bootstrap interval, or the reason it has neither.</summary>
public sealed class BenchmarkPairwiseRunScoreResponse
{
    public Guid RunId { get; init; }
    public int? Score { get; init; }
    public int? CiLow { get; init; }
    public int? CiHigh { get; init; }
    public int Comparisons { get; init; }
    public int BootstrapAppearances { get; init; }

    /// <summary>Null when the score ranks; otherwise the <c>pairwise-*</c> exclusion reason.</summary>
    public string? Reason { get; init; }
}

/// <summary>
///     The active Bradley–Terry fit of the project's current cohort. Served BESIDE the verdicts it was fit from, never
///     on a route of its own: a score rendered next to a verdict set that did not produce it is a lie the UI cannot
///     detect.
/// </summary>
public sealed class BenchmarkPairwiseFitResponse
{
    public required string FitKey { get; init; }
    public required string JudgeExecutionKey { get; init; }
    public int ComparisonSetVersion { get; init; }
    public int CohortGeneration { get; init; }
    public int Iterations { get; init; }
    public int BootstrapReplicates { get; init; }

    /// <summary>False when the cohort has changed since this fit, i.e. every run in it reads <c>pairwise-stale</c>.</summary>
    public bool IsCurrent { get; init; }

    public long CreatedAtUtc { get; init; }

    /// <summary>The ordered <c>(runAId, runBId, order, verdict)</c> tuples the fit actually used, as canonical JSON.</summary>
    public required string FittedSetJson { get; init; }

    public IReadOnlyList<BenchmarkPairwiseRunScoreResponse> Scores { get; init; } = [];
}

public sealed class ListBenchmarkComparisonsResponse
{
    public int CohortGeneration { get; init; }
    public int ComparisonSetVersion { get; init; }
    public string? ReferenceExecutionKey { get; init; }
    public IReadOnlyList<BenchmarkComparisonResponse> Items { get; init; } = [];
    public BenchmarkPairwiseFitResponse? Fit { get; init; }
}

public sealed class GetBenchmarkPairwiseEstimateRequest
{
    public Guid ProjectId { get; init; }
}

/// <summary>
///     What switching this project to pairwise will cost, before the operator commits. Pairwise judging is quadratic:
///     twelve runs is 132 judge calls.
/// </summary>
public sealed class GetBenchmarkPairwiseEstimateResponse
{
    public int EligibleRuns { get; init; }
    public int PairedRuns { get; init; }
    public int CappedRuns { get; init; }
    public int JudgeCalls { get; init; }

    /// <summary>Null when no judge attempt of this project has completed — omitted rather than guessed.</summary>
    public double? EstimatedSeconds { get; init; }

    /// <summary>Whether the cohort is large enough that the operator should see this before saving.</summary>
    public bool Warn { get; init; }

    public int MaximumRuns { get; init; }
}
