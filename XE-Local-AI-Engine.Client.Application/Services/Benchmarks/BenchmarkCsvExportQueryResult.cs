namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class BenchmarkCsvExportQueryResult
{
    public required BenchmarkProjectRecord Project { get; init; }

    public required IReadOnlyList<BenchmarkRunRecord> Runs { get; init; }

    public required BenchmarkPairwiseFitRecord? PairwiseFit { get; init; }

    /// <summary>The fit's per-run scores; empty without a fit or when its stored scores are unreadable.</summary>
    public required IReadOnlyList<BenchmarkPairwiseScoreEntry> PairwiseScores { get; init; }

    public required BenchmarkFidelityDisplayFacts Fidelity { get; init; }
}
