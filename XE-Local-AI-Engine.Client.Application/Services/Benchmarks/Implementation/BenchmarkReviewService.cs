namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkReviewService
{
    /// <summary>
    ///     Records a reviewer's decision on one scoring run of a review project, scored against the project's CURRENT rubric.
    /// </summary>
    /// <remarks>
    ///     Under a judge policy the reviewer scores every <c>llm</c> criterion 0..10; a verifiable criterion may be omitted or repeated, but never changed, and the
    ///     overall is computed here with the rubric's weights, so <see cref="BenchmarkReviewCommand.Score" /> is ignored. Without a policy the reviewer gives the
    ///     overall 0..100 alone. <see cref="BenchmarkReviewCommand.PolicyRevisionId" /> is always stamped here.
    /// </remarks>
    /// <exception cref="BenchmarkValidationException">The scores do not fit the rubric, or change a verifier's result.</exception>
    /// <exception cref="BenchmarkConflictException">
    ///     <c>NotReviewable</c> when the run's current judging has not succeeded under the current revision; the store's own refusals pass through.
    /// </exception>
    Task<BenchmarkRunRecord> SubmitAsync(BenchmarkReviewCommand command, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BenchmarkReviewService : IBenchmarkReviewService
{
    private readonly IBenchmarkStore _store;

    public BenchmarkReviewService(IBenchmarkStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<BenchmarkRunRecord> SubmitAsync(BenchmarkReviewCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var run = await _store.GetRunAsync(command.RunId, cancellationToken)
                  ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
        var revision = await _store.GetCurrentJudgePolicyRevisionAsync(run.ProjectId, cancellationToken);
        var policy = BenchmarkJudgeSerialization.DeserializeRevisionPolicy(revision);
        if (revision is null || policy is null)
        {
            if (command.Criteria is { Count: > 0 })
            {
                throw new BenchmarkValidationException("A project without a judge policy is reviewed with one overall score, not per criterion.");
            }

            return await _store.SubmitReviewAsync(command with
            {
                PolicyRevisionId = null
            }, cancellationToken);
        }

        // A human does not overrule a deterministic check, and a judging under an older revision checked a rubric the reviewer is no longer scoring.
        if (run.Judge is not { State: BenchmarkRunJudgeStates.Succeeded, AttemptId: { } attemptId } || run.Judge.PolicyRevisionId != revision.Id)
        {
            throw new BenchmarkConflictException("NotReviewable");
        }

        var criteria = await ScoreRubricAsync(policy.Rubric, command.Criteria ?? [], command.State == BenchmarkReviewStates.Approved, attemptId, cancellationToken);
        int? overall = null;
        if (command.State == BenchmarkReviewStates.Approved)
        {
            overall = criteria is null
                ? throw new BenchmarkValidationException("An approval scores every llm criterion of the rubric.")
                : BenchmarkJudgeScoreCalculator.Compute(policy.Rubric, criteria);
        }

        return await _store.SubmitReviewAsync(command with
        {
            Score = overall,
            Criteria = criteria?.Select(static score => new BenchmarkReviewCriterionScore
                               {
                                   CriterionId = score.Id,
                                   Score = score.Score
                               })
                               .ToArray(),
            PolicyRevisionId = revision.Id
        }, cancellationToken);
    }

    /// <summary>
    ///     The rubric's scores: the reviewer's for the <c>llm</c> criteria (every one when approving), the stored verifier's for the rest. <see langword="null" />
    ///     when nothing was submitted, which only a rejection may do.
    /// </summary>
    private async Task<IReadOnlyList<BenchmarkJudgeCriterionScoreV2>?> ScoreRubricAsync(BenchmarkJudgeRubricV1 rubric,
        IReadOnlyList<BenchmarkReviewCriterionScore> submitted,
        bool requireEveryLlmScore,
        Guid attemptId,
        CancellationToken cancellationToken)
    {
        // A rubric of verifiers alone has nothing for the reviewer to score: its approval stands on the verifier values.
        var verifierOnly = rubric.Criteria.All(static criterion => BenchmarkJudgeCriterionKinds.IsVerifiable(criterion.Kind));
        if (submitted.Count == 0 && !(requireEveryLlmScore && verifierOnly))
        {
            return null;
        }

        var rubricIds = rubric.Criteria.Select(static criterion => criterion.Id).ToArray();
        if (submitted.Any(score => score.Score is < BenchmarkJudgeOutputSchemaV2.MinimumCriterionScore or > BenchmarkJudgeOutputSchemaV2.MaximumCriterionScore
                                   || !rubricIds.Contains(score.CriterionId, StringComparer.Ordinal))
            || submitted.Select(static score => score.CriterionId).Distinct(StringComparer.Ordinal).Count() != submitted.Count)
        {
            throw new BenchmarkValidationException("Each criterion of the current rubric is scored once, between 0 and 10.");
        }

        var byId = submitted.ToDictionary(static score => score.CriterionId, static score => score.Score, StringComparer.Ordinal);
        var verified = await LoadVerifierScoresAsync(rubric, attemptId, cancellationToken);
        List<BenchmarkJudgeCriterionScoreV2> scores = [];
        foreach (var id in rubricIds)
        {
            var given = byId.TryGetValue(id, out var value) ? value : (int?)null;
            if (verified.TryGetValue(id, out var verifier))
            {
                if (given is { } changed && changed != verifier)
                {
                    throw new BenchmarkValidationException($"Criterion '{id}' is decided by its verifier and cannot be changed by a review.");
                }

                scores.Add(new BenchmarkJudgeCriterionScoreV2(id, verifier, string.Empty));
                continue;
            }

            // A rejection computes no overall, so it may score only what it rejects on.
            if (given is { } score)
            {
                scores.Add(new BenchmarkJudgeCriterionScoreV2(id, score, string.Empty));
            }
            else if (requireEveryLlmScore)
            {
                throw new BenchmarkValidationException($"Criterion '{id}' needs a score between 0 and 10.");
            }
        }

        return scores;
    }

    /// <summary>Each verifiable criterion's stored 0/10, read off the judging the reviewer is looking at.</summary>
    private async Task<IReadOnlyDictionary<string, int>> LoadVerifierScoresAsync(BenchmarkJudgeRubricV1 rubric, Guid attemptId, CancellationToken cancellationToken)
    {
        var verifiable = rubric.Criteria.Where(static criterion => BenchmarkJudgeCriterionKinds.IsVerifiable(criterion.Kind))
                               .Select(static criterion => criterion.Id)
                               .ToArray();
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        if (verifiable.Length == 0)
        {
            return scores;
        }

        var attempt = await _store.GetJudgeAttemptAsync(attemptId, cancellationToken);
        var results = BenchmarkJudgeSerialization.DeserializeResult(attempt?.ResultJson)?.Verifiers ?? [];
        foreach (var id in verifiable)
        {
            // A succeeded judging under this revision verified every verifiable criterion; one that did not cannot be reviewed against.
            var result = results.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
                         ?? throw new BenchmarkConflictException("NotReviewable");
            scores[id] = result.Passed ? BenchmarkJudgeVerifiers.PassScore : BenchmarkJudgeVerifiers.FailScore;
        }

        return scores;
    }
}
