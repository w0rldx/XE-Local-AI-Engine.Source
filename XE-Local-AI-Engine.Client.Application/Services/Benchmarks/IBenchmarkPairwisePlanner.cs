namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public interface IBenchmarkPairwisePlanner
{
    /// <summary>
    ///     Brings the project's pairwise cohort up to date: both orders of every unordered pair of its eligible runs.
    /// </summary>
    /// <remarks>
    ///     A no-op unless the project's current judge policy is in pairwise mode. Idempotent and incremental — adding
    ///     one run to a group of N enqueues 2N new comparisons, not N(N+1).
    /// </remarks>
    /// <returns>How many comparisons this call enqueued.</returns>
    Task<int> EnsurePairsAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>Startup reconciliation.</summary>
    /// <remarks>
    ///     A crash between "a primary succeeded" and "its pairs were enqueued" would otherwise leave a cohort
    ///     permanently one comparison short, and every run in it stuck on <c>pairwise-pending</c> with nothing that
    ///     would ever notice. Idempotent, so re-running it costs one read per judged project.
    /// </remarks>
    Task ReconcilePairwiseAsync(CancellationToken cancellationToken);

    /// <summary>The call count and ETA for the project's current eligible set.</summary>
    Task<BenchmarkPairwiseEstimate> EstimateAsync(Guid projectId, CancellationToken cancellationToken);
}
