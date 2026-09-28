namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The cohort limits pairwise judging runs under. Small numbers, because the cost is quadratic.</summary>
public static class BenchmarkPairwisePolicy
{
    /// <summary>Eligible runs per cohort.</summary>
    /// <remarks>
    ///     Twelve runs is 12·11 = 132 judge calls, which at a 32B judge's ~40 s a call is already ~90 minutes of GPU
    ///     time for ONE project. Past the cap nothing new is paired and the excess runs say so: a sampled
    ///     sub-tournament would be a silently biased one, and a refusal an operator can see beats it.
    /// </remarks>
    public const int MaximumRuns = 12;

    /// <summary>At or above this, the pre-flight estimate is worth putting in front of the operator before they commit.</summary>
    public const int WarnAtRuns = 8;

    /// <summary>
    ///     The share of fitted verdicts that may have had a truncated side before the cohort refuses to aggregate.
    /// </summary>
    /// <remarks>
    ///     Each answer is bounded to half the judge window in pairwise mode, so a long answer is cut harder here than
    ///     it is pointwise — which is itself a bias, and one worth refusing rather than publishing.
    /// </remarks>
    public const double MaximumTruncatedShare = 0.20;
}
