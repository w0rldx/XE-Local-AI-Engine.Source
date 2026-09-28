namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The pre-flight an operator sees before switching a project to pairwise.</summary>
public sealed class BenchmarkPairwiseEstimate
{
    public required int EligibleRuns { get; init; }

    public required int PairedRuns { get; init; }

    public required int CappedRuns { get; init; }

    public required int JudgeCalls { get; init; }

    /// <summary>
    ///     Null when no judge attempt of this project has completed. The estimate is omitted rather than guessed: a made-up
    ///     ETA in front of a ninety-minute commitment is worse than none.
    /// </summary>
    public required double? EstimatedSeconds { get; init; }

    public required bool Warn { get; init; }
}
