namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1;

public sealed class CreateComparisonRequest
{
    public required string Name { get; init; }
    public required Guid BaseEvaluationRunId { get; init; }
    public required Guid TunedEvaluationRunId { get; init; }

    /// <summary>Optional throughput/quality pairing. Read-only — nothing here starts or re-runs a benchmark.</summary>
    public Guid? BaseBenchmarkRunId { get; init; }

    public Guid? TunedBenchmarkRunId { get; init; }
    public Guid? TrainingRunId { get; init; }
}

public sealed class ComparisonByIdRequest
{
    public required Guid ComparisonId { get; init; }
}

/// <summary>
///     The id is route-bound and the version comes from the body; neither can be <c>required</c>, because the body
///     is deserialized before the route value is applied and the generated client sends only <c>expectedVersion</c>.
/// </summary>
public sealed class DeleteComparisonRequest
{
    public Guid ComparisonId { get; init; }

    public long ExpectedVersion { get; init; }
}

public sealed class SuggestComparisonRequest
{
    public required Guid TrainingRunId { get; init; }
}

public sealed class ComparisonKindDeltaResponse
{
    public required string Kind { get; init; }
    public required int BaseTotal { get; init; }
    public required int BasePassed { get; init; }
    public required int TunedTotal { get; init; }
    public required int TunedPassed { get; init; }
    public required double BaseAccuracy { get; init; }
    public required double TunedAccuracy { get; init; }
    public required double AccuracyDelta { get; init; }
}

public sealed class ComparisonBenchmarkDeltaResponse
{
    public double? BaseTokensPerSecond { get; init; }
    public double? TunedTokensPerSecond { get; init; }
    public double? TokensPerSecondDelta { get; init; }
    public long? BaseDurationMs { get; init; }
    public long? TunedDurationMs { get; init; }
    public int? BaseUserScore { get; init; }
    public int? TunedUserScore { get; init; }
    public int? UserScoreDelta { get; init; }
    public int? BaseJudgeScore { get; init; }
    public int? TunedJudgeScore { get; init; }
    public int? JudgeScoreDelta { get; init; }
}

public sealed class ComparisonDeltasResponse
{
    public required string BaseModelName { get; init; }
    public required string TunedModelName { get; init; }
    public required int BaseScoredCount { get; init; }
    public required int BasePassedCount { get; init; }
    public required int TunedScoredCount { get; init; }
    public required int TunedPassedCount { get; init; }
    public required double BaseAccuracy { get; init; }
    public required double TunedAccuracy { get; init; }
    public required double AccuracyDelta { get; init; }
    public required IReadOnlyList<ComparisonKindDeltaResponse> PerKind { get; init; }

    /// <summary>False when either side scored nothing; the accuracy section renders as unavailable rather than as 0%.</summary>
    public required bool AccuracyAvailable { get; init; }

    public string? UnavailableReason { get; init; }
    public ComparisonBenchmarkDeltaResponse? Benchmark { get; init; }
}

public sealed class ComparisonResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required Guid BaseEvaluationRunId { get; init; }
    public required Guid TunedEvaluationRunId { get; init; }
    public Guid? BaseBenchmarkRunId { get; init; }
    public Guid? TunedBenchmarkRunId { get; init; }
    public Guid? TrainingRunId { get; init; }
    public ComparisonDeltasResponse? Deltas { get; init; }
    public required long Version { get; init; }
    public required long CreatedAtUtc { get; init; }
    public required long UpdatedAtUtc { get; init; }
}

public sealed class ListComparisonsResponse
{
    public required IReadOnlyList<ComparisonResponse> Items { get; init; }
}

public sealed class ComparisonSuggestionResponse
{
    public required Guid TrainingRunId { get; init; }
    public string? BaseModelName { get; init; }
    public string? TunedModelName { get; init; }
    public Guid? BaseEvaluationRunId { get; init; }
    public Guid? TunedEvaluationRunId { get; init; }

    /// <summary>Why one of the two sides cannot be produced yet — a run with no installed base, or nothing promoted.</summary>
    public string? UnavailableReason { get; init; }
}
