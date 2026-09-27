namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Everything a project detail carries beyond the project row: the decrypted judge policy and the task items.</summary>
public sealed class BenchmarkProjectDetail
{
    public required BenchmarkProjectRecord Project { get; init; }

    public required BenchmarkJudgePolicyRevisionRecord? JudgePolicyRevision { get; init; }

    /// <summary>The revision's policy, decoded; null when judging is off or the revision carries no payload.</summary>
    public required BenchmarkJudgePolicyV1? JudgePolicy { get; init; }

    public required IReadOnlyList<BenchmarkTaskItemRecord> TaskItems { get; init; }
}

/// <summary>The cells a caller named, in the order named, with the paired delta between every pair that shares enough items.</summary>
public sealed class BenchmarkCellComparison
{
    /// <summary>Only the selected cells; the cohort and scorable-item count are the project's own.</summary>
    public required BenchmarkCellPage Cells { get; init; }

    public required IReadOnlyList<BenchmarkCellPairDelta> PairedDeltas { get; init; }
}

public sealed class BenchmarkCellPairDelta
{
    public required string ACellKey { get; init; }

    public required string BCellKey { get; init; }

    public required BenchmarkPairedDelta Estimate { get; init; }
}

/// <summary>A project's pairwise verdict matrix together with the fit those verdicts produced.</summary>
public sealed class BenchmarkComparisonsView
{
    public required BenchmarkPairwiseCohortState Cohort { get; init; }

    public required BenchmarkPairwiseFitRecord? Fit { get; init; }

    /// <summary>The fit's per-run scores; empty without a fit or when its stored scores are unreadable.</summary>
    public required IReadOnlyList<BenchmarkPairwiseScoreEntry> FitScores { get; init; }

    /// <summary>Whether the fit was made over the cohort's current comparison set with its promoted judge execution.</summary>
    public required bool FitIsCurrent { get; init; }
}
