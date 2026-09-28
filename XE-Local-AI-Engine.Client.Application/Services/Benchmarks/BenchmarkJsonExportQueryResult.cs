namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class BenchmarkJsonExportQueryResult
{
    public required BenchmarkProjectRecord Project { get; init; }

    public required IReadOnlyList<BenchmarkRunRecord> Summaries { get; init; }

    public required IReadOnlyList<BenchmarkExportRunQueryItem> Runs { get; init; }

    public required BenchmarkRankCohort? RankCohort { get; init; }

    public required BenchmarkJudgePolicyRevisionRecord? JudgePolicyRevision { get; init; }

    public required BenchmarkPairwiseFitRecord? PairwiseFit { get; init; }

    /// <summary>The fit's per-run scores; empty without a fit or when its stored scores are unreadable.</summary>
    public required IReadOnlyList<BenchmarkPairwiseScoreEntry> PairwiseScores { get; init; }

    public required BenchmarkFidelityDisplayFacts Fidelity { get; init; }

    public required IReadOnlyDictionary<Guid, BenchmarkExportRunFacts> Facts { get; init; }

    public required IReadOnlyList<BenchmarkTaskItemRecord> TaskItems { get; init; }

    public required BenchmarkCellPage Cells { get; init; }
}
