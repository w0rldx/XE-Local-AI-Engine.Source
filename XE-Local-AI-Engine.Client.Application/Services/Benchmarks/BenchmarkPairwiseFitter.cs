namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkPairwiseFitter
{
    /// <summary>
    ///     Fits and publishes the project's pairwise cohort, if it is complete and not already published. Called after
    ///     every comparison terminalization and from startup reconciliation; a no-op until the LAST comparison of the
    ///     cohort lands, because a fit over a partial tournament is a different tournament.
    /// </summary>
    /// <returns><see langword="true" /> when this call published a fit.</returns>
    Task<bool> TryPublishAsync(Guid projectId, CancellationToken cancellationToken);
}

/// <summary>
///     Turns a completed cohort of pairwise verdicts into ONE immutable fit row with ONE active pointer.
/// </summary>
/// <remarks>
///     Three refusals sit in front of the arithmetic, and all three publish a row that carries the REASON and no
///     scores rather than publishing nothing at all. A fit that silently fails to appear is indistinguishable, on the
///     ranking read, from a cohort still judging — and telling those apart without re-reading every verdict on every
///     page fetch is exactly what the one-row design exists for. The refusals themselves are strict on purpose: a fit
///     blending two judge runtimes, or fitted over the subset that happens to match, would publish a number over a set
///     the operator never chose.
/// </remarks>
public sealed class BenchmarkPairwiseFitter(IBenchmarkStore store, ILogger<BenchmarkPairwiseFitter> logger) : IBenchmarkPairwiseFitter
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<BenchmarkPairwiseFitter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<bool> TryPublishAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var revision = await _store.GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (revision?.PolicyJson is not { } policyJson)
        {
            return false;
        }

        var policy = BenchmarkJudgeSerialization.DeserializePolicy(policyJson.Span);
        if (!string.Equals(BenchmarkJudgePolicyModes.Normalize(policy.Mode), BenchmarkJudgePolicyModes.Pairwise, StringComparison.Ordinal))
        {
            return false;
        }

        var cohort = await _store.GetPairwiseCohortAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (cohort.PolicyRevisionId is not { } revisionId || !IsComplete(cohort))
        {
            return false;
        }

        var succeeded = cohort.Comparisons.Where(static comparison => comparison.Status == BenchmarkJudgeAttemptStatus.Succeeded
                                                                     && comparison.Verdict is not null)
                              .OrderBy(static comparison => comparison.Sequence)
                              .ToArray();
        var fitKey = ComputeFitKey(policy, revisionId, cohort);
        var active = await _store.GetActivePairwiseFitAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (string.Equals(active?.FitKey, fitKey, StringComparison.Ordinal))
        {
            return false;
        }

        var refusal = Refuse(cohort, succeeded);
        var fit = refusal is null
            ? BenchmarkBradleyTerry.Fit([.. succeeded.Select(static comparison => new BenchmarkPairwiseVerdict(comparison.RunAId, comparison.RunBId, comparison.Verdict!))])
            : null;
        var scores = ToScoreEntries(cohort, fit, refusal ?? fit?.Refusal);
        var command = new BenchmarkPairwiseFitCommand(projectId,
            revisionId,
            cohort.CohortGeneration,
            TaskCaseId: null,
            fitKey,
            cohort.ReferenceExecutionKey ?? string.Empty,
            cohort.ComparisonSetVersion,
            BenchmarkCanonicalJson.Serialize(succeeded.Select(static comparison => new FittedVerdict(comparison.RunAId,
                comparison.RunBId,
                comparison.Order,
                comparison.Verdict!))),
            BenchmarkCanonicalJson.Serialize(scores),

            // The shipped CHECK requires both to be positive, so a refusal records the sweep budget it exhausted and
            // the replicate budget it was configured with rather than a zero the row cannot hold.
            fit is { Iterations: > 0 } ? fit.Iterations : BenchmarkBradleyTerry.MaximumIterations,
            BenchmarkBradleyTerry.DefaultReplicates);
        var published = await _store.PublishPairwiseFitAsync(command, cancellationToken).ConfigureAwait(false);
        if (published)
        {
            _logger.LogInformation("Benchmark project {ProjectId}: published pairwise fit over {VerdictCount} verdicts{Refusal}.",
                projectId,
                succeeded.Length,
                refusal is null ? string.Empty : $" as a refusal ({refusal})");
        }

        return published;
    }

    /// <summary>
    ///     A cohort is complete when BOTH presentation orders of every planned pair carry a succeeded comparison. A
    ///     failed or cancelled one leaves its slot free, so the cohort stays incomplete — and every run in it reads
    ///     <c>pairwise-pending</c> until reconciliation re-enqueues that slot at the next attempt sequence.
    /// </summary>
    private static bool IsComplete(BenchmarkPairwiseCohortState cohort)
    {
        var plan = BenchmarkPairwisePlanner.Plan(cohort.Candidates, BenchmarkPairwisePolicy.MaximumRuns);
        if (plan.Slots.Count == 0)
        {
            return false;
        }

        var succeeded = cohort.Comparisons.Where(static comparison => comparison.Status == BenchmarkJudgeAttemptStatus.Succeeded)
                              .Select(static comparison => (comparison.RunAId, comparison.RunBId, comparison.Order))
                              .ToHashSet();
        return plan.Slots.All(slot => succeeded.Contains((slot.RunAId, slot.RunBId, 0)) && succeeded.Contains((slot.RunAId, slot.RunBId, 1)));
    }

    /// <summary>
    ///     The gates that run BEFORE the arithmetic. Each refuses the whole fit: nothing is published as a score, and
    ///     no partial fit over the comparisons that happen to qualify is attempted, because dropping comparisons
    ///     changes the comparison graph — possibly disconnecting it — under a set nobody chose.
    /// </summary>
    private static string? Refuse(BenchmarkPairwiseCohortState cohort, IReadOnlyList<BenchmarkComparisonRecord> succeeded)
    {
        if (cohort.ReferenceExecutionKey is not { } reference)
        {
            return BenchmarkRunJudgeStates.ReasonPairwiseExecutionIdentityIncomplete;
        }

        if (succeeded.Any(comparison => !string.Equals(comparison.JudgeExecutionKey, reference, StringComparison.Ordinal)))
        {
            return BenchmarkRunJudgeStates.ReasonPairwiseExecutionMismatch;
        }

        var truncated = succeeded.Count(static comparison => comparison.AnswerATruncated || comparison.AnswerBTruncated);
        return succeeded.Count > 0 && truncated > succeeded.Count * BenchmarkPairwisePolicy.MaximumTruncatedShare
            ? BenchmarkRunJudgeStates.ReasonPairwiseInsufficient
            : null;
    }

    /// <summary>
    ///     One entry per ELIGIBLE run, not one per fitted run: a run the cap left out, or one the graph stranded, must
    ///     say why it has no score, and the ranking read has only this row to learn it from.
    /// </summary>
    private static BenchmarkPairwiseScoreEntry[] ToScoreEntries(BenchmarkPairwiseCohortState cohort, BenchmarkBradleyTerryFit? fit, string? refusal)
    {
        var plan = BenchmarkPairwisePlanner.Plan(cohort.Candidates, BenchmarkPairwisePolicy.MaximumRuns);
        var capped = plan.CappedRunIds.ToHashSet();
        var fitted = fit?.Scores.ToDictionary(static score => score.RunId) ?? [];
        return [.. cohort.Candidates.Select(candidate => ToEntry(candidate.RunId, fitted, capped, refusal))];
    }

    private static BenchmarkPairwiseScoreEntry ToEntry(Guid runId,
        IReadOnlyDictionary<Guid, BenchmarkPairwiseRunScore> fitted,
        IReadOnlySet<Guid> capped,
        string? refusal)
    {
        if (refusal is not null)
        {
            return new BenchmarkPairwiseScoreEntry(runId, null, null, null, 0, 0, refusal);
        }

        if (capped.Contains(runId))
        {
            return new BenchmarkPairwiseScoreEntry(runId, null, null, null, 0, 0, BenchmarkRunJudgeStates.ReasonPairwiseCap);
        }

        return fitted.TryGetValue(runId, out var score)
            ? new BenchmarkPairwiseScoreEntry(runId, score.Score, score.CiLow, score.CiHigh, score.Comparisons, score.BootstrapAppearances, score.Reason)
            : new BenchmarkPairwiseScoreEntry(runId, null, null, null, 0, 0, BenchmarkRunJudgeStates.ReasonPairwiseInsufficient);
    }

    /// <summary>
    ///     The fit's durable identity. Every input describing what was ASKED — revision, generation, policy hash, both
    ///     pairwise versions, the case — plus the one describing what ANSWERED: the cohort's promoted judge execution
    ///     key. A generation counter alone cannot tell a reader whether the fit behind a stored score used the same
    ///     verdicts, the same prompt, the same case or the same judge runtime. The comparison-set VERSION rather than a
    ///     hash of the verdicts, so the read path compares one integer instead of re-hashing every verdict per page —
    ///     and it is strictly stronger: a cancel-then-re-enqueue landing on identical verdicts still bumps it.
    /// </summary>
    private static string ComputeFitKey(BenchmarkJudgePolicyV1 policy, Guid revisionId, BenchmarkPairwiseCohortState cohort) =>
        "v1:" + BenchmarkCanonicalJson.HashOf(new
        {
            policyRevisionId = revisionId,
            cohortGeneration = cohort.CohortGeneration,
            policyHash = BenchmarkJudgePolicyCanonicalizer.ComputePolicyHash(policy),
            pairwisePromptVersion = policy.PairwisePromptVersion,
            pairwiseOutputSchemaVersion = policy.PairwiseOutputSchemaVersion,
            taskCaseId = (Guid?)null,
            taskInputHash = string.Empty,
            judgeExecutionKey = cohort.ReferenceExecutionKey ?? string.Empty,
            comparisonSetVersion = cohort.ComparisonSetVersion
        });

    /// <summary>One row of the auditable answer to "which verdicts produced this number".</summary>
    private sealed record FittedVerdict(Guid RunAId, Guid RunBId, int Order, string Verdict);
}
