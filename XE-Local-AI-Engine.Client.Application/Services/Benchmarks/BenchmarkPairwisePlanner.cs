namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The cohort limits pairwise judging runs under. Small numbers, because the cost is quadratic.</summary>
public static class BenchmarkPairwisePolicy
{
    /// <summary>
    ///     Eligible runs per cohort. Twelve runs is 12·11 = 132 judge calls, which at a 32B judge's ~40 s a call is
    ///     already ~90 minutes of GPU time for ONE project. Past the cap nothing new is paired and the excess runs say
    ///     so: a sampled sub-tournament would be a silently biased one, and a refusal an operator can see beats it.
    /// </summary>
    public const int MaximumRuns = 12;

    /// <summary>At or above this, the pre-flight estimate is worth putting in front of the operator before they commit.</summary>
    public const int WarnAtRuns = 8;

    /// <summary>
    ///     The share of fitted verdicts that may have had a truncated side before the cohort refuses to aggregate.
    ///     Each answer is bounded to half the judge window in pairwise mode, so a long answer is cut harder here than
    ///     it is pointwise — which is itself a bias, and one worth refusing rather than publishing.
    /// </summary>
    public const double MaximumTruncatedShare = 0.20;
}

/// <summary>Which pairs a cohort should hold, and which runs it had to leave out.</summary>
public sealed record BenchmarkPairwisePlan(
    IReadOnlyList<BenchmarkPairwiseSlot> Slots,
    IReadOnlyList<Guid> PairedRunIds,
    IReadOnlyList<Guid> CappedRunIds);

/// <summary>The pre-flight an operator sees before switching a project to pairwise.</summary>
/// <param name="EstimatedSeconds">
///     Null when no judge attempt of this project has completed. The estimate is omitted rather than guessed: a made-up
///     ETA in front of a ninety-minute commitment is worse than none.
/// </param>
public sealed record BenchmarkPairwiseEstimate(
    int EligibleRuns,
    int PairedRuns,
    int CappedRuns,
    int JudgeCalls,
    double? EstimatedSeconds,
    bool Warn);

public interface IBenchmarkPairwisePlanner
{
    /// <summary>
    ///     Brings the project's pairwise cohort up to date: both orders of every unordered pair of its eligible runs.
    ///     A no-op unless the project's current judge policy is in pairwise mode. Idempotent and incremental — adding
    ///     one run to a group of N enqueues 2N new comparisons, not N(N+1).
    /// </summary>
    /// <returns>How many comparisons this call enqueued.</returns>
    Task<int> EnsurePairsAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    ///     Startup reconciliation. A crash between "a primary succeeded" and "its pairs were enqueued" would otherwise
    ///     leave a cohort permanently one comparison short, and every run in it stuck on <c>pairwise-pending</c> with
    ///     nothing that would ever notice. Idempotent, so re-running it costs one read per judged project.
    /// </summary>
    Task ReconcilePairwiseAsync(CancellationToken cancellationToken);

    /// <summary>The call count and ETA for the project's current eligible set.</summary>
    Task<BenchmarkPairwiseEstimate> EstimateAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class BenchmarkPairwisePlanner(
    IBenchmarkStore store,
    IBenchmarkJudgeRuntimeResolver judgeRuntimeResolver,
    IBenchmarkPairwiseFitter fitter,
    IBenchmarkQueueSignal queueSignal,
    ILogger<BenchmarkPairwisePlanner> logger) : IBenchmarkPairwisePlanner
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly IBenchmarkJudgeRuntimeResolver _judgeRuntimeResolver =
        judgeRuntimeResolver ?? throw new ArgumentNullException(nameof(judgeRuntimeResolver));

    private readonly IBenchmarkPairwiseFitter _fitter = fitter ?? throw new ArgumentNullException(nameof(fitter));
    private readonly IBenchmarkQueueSignal _queueSignal = queueSignal ?? throw new ArgumentNullException(nameof(queueSignal));
    private readonly ILogger<BenchmarkPairwisePlanner> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    ///     Every unordered pair of the eligible set, formed ONLY inside one task case. Two answers to different
    ///     questions were never comparable, so a candidate whose case identity differs is in a different group and is
    ///     never paired across. A single-case project naturally produces one group; a suite relies on the same stored
    ///     identity to keep its cases separate without reinterpreting existing comparisons.
    /// </summary>
    /// <param name="maximumRuns">The cohort cap; candidates past it are returned as capped and never paired.</param>
    public static BenchmarkPairwisePlan Plan(IReadOnlyList<BenchmarkPairwiseCandidate> candidates, int maximumRuns)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var paired = candidates.Take(maximumRuns).ToArray();
        var slots = new List<BenchmarkPairwiseSlot>();
        foreach (var group in paired.GroupBy(static candidate => (candidate.TaskCaseId, candidate.TaskInputHash)))
        {
            var members = group.ToArray();
            for (var first = 0; first < members.Length; first++)
            {
                for (var second = first + 1; second < members.Length; second++)
                {
                    var (runA, runB) = members[first].RunId.CompareTo(members[second].RunId) < 0
                        ? (members[first].RunId, members[second].RunId)
                        : (members[second].RunId, members[first].RunId);
                    slots.Add(new BenchmarkPairwiseSlot(runA, runB, group.Key.TaskCaseId, group.Key.TaskInputHash));
                }
            }
        }

        return new BenchmarkPairwisePlan(slots,
            [.. paired.Select(static candidate => candidate.RunId)],
            [.. candidates.Skip(maximumRuns).Select(static candidate => candidate.RunId)]);
    }

    public async Task<int> EnsurePairsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var policy = await ReadPairwisePolicyAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            return 0;
        }

        var cohort = await _store.GetPairwiseCohortAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (cohort.PolicyRevisionId is null)
        {
            return 0;
        }

        var plan = Plan(cohort.Candidates, BenchmarkPairwisePolicy.MaximumRuns);
        if (plan.Slots.Count == 0)
        {
            return 0;
        }

        // The judge runtime is resolved ONCE for the whole cohort, exactly as the pointwise seed resolves it once for
        // the revision: it depends only on the policy, and resolving per pair could straddle a runtime swap mid-loop
        // and split one cohort's verdicts across two execution identities — which the fit then refuses outright.
        BenchmarkJudgeRuntimeResolution resolution;
        try
        {
            resolution = await _judgeRuntimeResolver.ResolveAsync(policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is BenchmarkEligibilityException
                                              or BenchmarkUnsupportedKvCacheTypeException
                                              or BenchmarkSnapshotException
                                              or KeyNotFoundException)
        {
            // Nothing is enqueued: a comparison with no runtime could only fail, and a failed comparison holds no slot
            // and tells the operator nothing the next attempt would not. The cohort stays pending and re-tries on the
            // next primary success or restart, by which time the judge model may be back.
            _logger.LogWarning(exception, "Benchmark project {ProjectId}: the judge runtime is unresolved, so no pairwise comparisons were enqueued.", projectId);
            return 0;
        }

        var created = await _store.EnsureComparisonsAsync(projectId,
                                      plan.Slots,
                                      new ReadOnlyMemory<byte>(BenchmarkJudgeSerialization.SerializeRuntime(resolution.Runtime)),
                                      resolution.Intent,
                                      cancellationToken)
                                  .ConfigureAwait(false);
        if (created > 0)
        {
            _queueSignal.Wake();
        }

        return created;
    }

    public async Task ReconcilePairwiseAsync(CancellationToken cancellationToken)
    {
        var projectIds = await _store.ListJudgedProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var projectId in projectIds)
        {
            try
            {
                _ = await EnsurePairsAsync(projectId, cancellationToken).ConfigureAwait(false);

                // A cohort whose comparisons all terminalized while the fit was being published — or before the
                // process died — has verdicts and no active fit. The fit is a pure function of stored verdicts, so
                // re-triggering it here is the whole of that recovery.
                _ = await _fitter.TryPublishAsync(projectId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is BenchmarkStoreException or BenchmarkExecutionException)
            {
                _logger.LogWarning(exception, "Benchmark project {ProjectId}: pairwise reconciliation failed and was skipped.", projectId);
            }
        }
    }

    public async Task<BenchmarkPairwiseEstimate> EstimateAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var cohort = await _store.GetPairwiseCohortAsync(projectId, cancellationToken).ConfigureAwait(false);
        var plan = Plan(cohort.Candidates, BenchmarkPairwisePolicy.MaximumRuns);
        var paired = plan.PairedRunIds.Count;
        var calls = paired * (paired - 1);
        var median = await _store.GetMedianJudgeDurationSecondsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return new BenchmarkPairwiseEstimate(cohort.Candidates.Count,
            paired,
            plan.CappedRunIds.Count,
            calls,
            median is { } seconds ? seconds * calls : null,
            paired >= BenchmarkPairwisePolicy.WarnAtRuns);
    }

    /// <summary>The project's current judge policy when it judges pairwise, otherwise <see langword="null" />.</summary>
    private async Task<BenchmarkJudgePolicyV1?> ReadPairwisePolicyAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var revision = await _store.GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (revision?.PolicyJson is not { } policyJson)
        {
            return null;
        }

        var policy = BenchmarkJudgeSerialization.DeserializePolicy(policyJson.Span);
        return string.Equals(BenchmarkJudgePolicyModes.Normalize(policy.Mode), BenchmarkJudgePolicyModes.Pairwise, StringComparison.Ordinal)
            ? policy
            : null;
    }
}
