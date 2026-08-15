namespace XE_Local_AI_Engine.Client.Services.Training.Comparison;

/// <summary>One sample kind's two accuracies and the difference between them.</summary>
public sealed record ComparisonKindDeltaV1(
    string Kind,
    int BaseTotal,
    int BasePassed,
    int TunedTotal,
    int TunedPassed,
    double BaseAccuracy,
    double TunedAccuracy,
    double AccuracyDelta);

/// <summary>
///     The optional throughput/quality pairing, read straight off two benchmark runs. Read-only: nothing here starts,
///     re-runs or mutates a benchmark — the pairing exists so one report can carry "it got better at the task" next to
///     "it got slower", which are different questions with different evidence.
/// </summary>
public sealed record ComparisonBenchmarkDeltaV1
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

/// <summary>
///     The stored comparison, persisted (encrypted) in <c>training_comparison_reports.deltas_json</c>. Every number
///     here is REPRODUCIBLE from the two bound evaluations' persisted results plus their frozen memberships — the
///     document is a cache of a pure computation, not a separate source of truth.
/// </summary>
public sealed record TrainingComparisonDeltasV1
{
    public int SchemaVersion { get; init; } = 1;

    public string BaseModelName { get; init; } = string.Empty;

    public string TunedModelName { get; init; } = string.Empty;

    public int BaseScoredCount { get; init; }

    public int BasePassedCount { get; init; }

    public int TunedScoredCount { get; init; }

    public int TunedPassedCount { get; init; }

    public double BaseAccuracy { get; init; }

    public double TunedAccuracy { get; init; }

    public double AccuracyDelta { get; init; }

    public IReadOnlyList<ComparisonKindDeltaV1> PerKind { get; init; } = [];

    /// <summary>
    ///     False when either side scored nothing — a base model that was never installed, or an evaluation that failed
    ///     before its first sample. The section is marked unavailable rather than rendered as a 0% score, which would
    ///     read as "the model got everything wrong".
    /// </summary>
    public bool AccuracyAvailable { get; init; }

    public string? UnavailableReason { get; init; }

    public ComparisonBenchmarkDeltaV1? Benchmark { get; init; }
}
