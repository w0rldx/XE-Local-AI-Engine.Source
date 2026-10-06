namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <inheritdoc />
public sealed class BenchmarkPairwisePlanner : IBenchmarkPairwisePlanner
{
    private readonly IBenchmarkStore _store;
    private readonly IBenchmarkJudgeRuntimeResolver _judgeRuntimeResolver;
    private readonly IBenchmarkPairwiseFitter _fitter;
    private readonly IBenchmarkQueueSignal _queueSignal;
    private readonly ILogger<BenchmarkPairwisePlanner> _logger;

    public BenchmarkPairwisePlanner(IBenchmarkStore store,
        IBenchmarkJudgeRuntimeResolver judgeRuntimeResolver,
        IBenchmarkPairwiseFitter fitter,
        IBenchmarkQueueSignal queueSignal,
        ILogger<BenchmarkPairwisePlanner> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(judgeRuntimeResolver);
        ArgumentNullException.ThrowIfNull(fitter);
        ArgumentNullException.ThrowIfNull(queueSignal);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _judgeRuntimeResolver = judgeRuntimeResolver;
        _fitter = fitter;
        _queueSignal = queueSignal;
        _logger = logger;
    }

    /// <summary>Every unordered pair of the eligible set, formed ONLY inside one task case.</summary>
    /// <remarks>
    ///     Two answers to different questions were never comparable, so a candidate whose case identity differs is in
    ///     a different group and is never paired across. A single-case project naturally produces one group; a suite
    ///     relies on the same stored identity to keep its cases separate without reinterpreting existing comparisons.
    /// </remarks>
    /// <param name="maximumRuns">The cohort cap; candidates past it are returned as capped and never paired.</param>
    public static BenchmarkPairwisePlan Plan(IReadOnlyList<BenchmarkPairwiseCandidate> candidates, int maximumRuns)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var paired = candidates.Take(maximumRuns).ToArray();
        var slots = new List<BenchmarkPairwiseSlot>();
        foreach (var group in paired.GroupBy(static candidate => new { candidate.TaskCaseId, candidate.TaskInputHash }))
        {
            var members = group.ToArray();
            for (var first = 0; first < members.Length; first++)
            {
                for (var second = first + 1; second < members.Length; second++)
                {
                    var firstIsLower = members[first].RunId.CompareTo(members[second].RunId) < 0;
                    var runA = firstIsLower ? members[first].RunId : members[second].RunId;
                    var runB = firstIsLower ? members[second].RunId : members[first].RunId;
                    slots.Add(new BenchmarkPairwiseSlot
                    {
                        RunAId = runA,
                        RunBId = runB,
                        TaskCaseId = group.Key.TaskCaseId,
                        TaskInputHash = group.Key.TaskInputHash
                    });
                }
            }
        }

        return new BenchmarkPairwisePlan
        {
            Slots = slots,
            PairedRunIds = [.. paired.Select(static candidate => candidate.RunId)],
            CappedRunIds = [.. candidates.Skip(maximumRuns).Select(static candidate => candidate.RunId)]
        };
    }

    public async Task<int> EnsurePairsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var policy = await ReadPairwisePolicyAsync(projectId, cancellationToken);
        if (policy is null)
        {
            return 0;
        }

        var cohort = await _store.GetPairwiseCohortAsync(projectId, cancellationToken);
        if (cohort.PolicyRevisionId is null)
        {
            return 0;
        }

        var plan = Plan(cohort.Candidates, BenchmarkPairwisePolicy.MaximumRuns);
        if (plan.Slots.Count == 0)
        {
            return 0;
        }

        // The judge runtime is resolved ONCE for the whole cohort, exactly as the pointwise seed resolves it once for the revision: it depends only on the policy.
        // Resolving per pair could straddle a runtime swap mid-loop and split one cohort's verdicts across two execution identities — which the fit then refuses outright.
        BenchmarkJudgeRuntimeResolution resolution;
        try
        {
            resolution = await _judgeRuntimeResolver.ResolveAsync(policy, cancellationToken);
        }
        catch (Exception exception) when (exception is BenchmarkEligibilityException
                                              or BenchmarkUnsupportedKvCacheTypeException
                                              or BenchmarkSnapshotException
                                              or KeyNotFoundException)
        {
            // Nothing is enqueued: a comparison with no runtime could only fail, and a failed comparison holds no slot and tells the operator nothing the next attempt would not.
            // The cohort stays pending and re-tries on the next primary success or restart, by which time the judge model may be back.
            _logger.LogWarning(exception, "Benchmark project {ProjectId}: the judge runtime is unresolved, so no pairwise comparisons were enqueued.", projectId);
            return 0;
        }

        var created = await _store.EnsureComparisonsAsync(projectId,
            plan.Slots,
            new ReadOnlyMemory<byte>(BenchmarkJudgeSerialization.SerializeRuntime(resolution.Runtime)),
            resolution.Intent,
            cancellationToken);
        if (created > 0)
        {
            _queueSignal.Wake();
        }

        return created;
    }

    public async Task ReconcilePairwiseAsync(CancellationToken cancellationToken)
    {
        var projectIds = await _store.ListJudgedProjectIdsAsync(cancellationToken);
        foreach (var projectId in projectIds)
        {
            try
            {
                _ = await EnsurePairsAsync(projectId, cancellationToken);

                // A cohort whose comparisons all terminalized while the fit was being published — or before the process died — has verdicts and no active fit.
                // The fit is a pure function of stored verdicts, so re-triggering it here is the whole of that recovery.
                _ = await _fitter.TryPublishAsync(projectId, cancellationToken);
            }
            catch (Exception exception) when (exception is BenchmarkStoreException or BenchmarkExecutionException)
            {
                _logger.LogWarning(exception, "Benchmark project {ProjectId}: pairwise reconciliation failed and was skipped.", projectId);
            }
        }
    }

    public async Task<BenchmarkPairwiseEstimate> EstimateAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var cohort = await _store.GetPairwiseCohortAsync(projectId, cancellationToken);
        var plan = Plan(cohort.Candidates, BenchmarkPairwisePolicy.MaximumRuns);
        var paired = plan.PairedRunIds.Count;
        var calls = paired * (paired - 1);
        var median = await _store.GetMedianJudgeDurationSecondsAsync(projectId, cancellationToken);
        return new BenchmarkPairwiseEstimate
        {
            EligibleRuns = cohort.Candidates.Count,
            PairedRuns = paired,
            CappedRuns = plan.CappedRunIds.Count,
            JudgeCalls = calls,
            EstimatedSeconds = median is { } seconds ? seconds * calls : null,
            Warn = paired >= BenchmarkPairwisePolicy.WarnAtRuns
        };
    }

    /// <summary>The project's current judge policy when it judges pairwise, otherwise <see langword="null" />.</summary>
    private async Task<BenchmarkJudgePolicyV1?> ReadPairwisePolicyAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var revision = await _store.GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken);
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
