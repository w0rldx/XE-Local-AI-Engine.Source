namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using static BenchmarkRankingPolicy;

public sealed partial class BenchmarkStore
{
    /// <summary>
    ///     A run record carrying its derived judge view.
    /// </summary>
    /// <remarks>
    ///     Every path that returns a run uses this: the view is how a caller reads judge state now, and a record that
    ///     silently omitted it would read as "no judging" to a caller that had just terminalized one.
    /// </remarks>
    private async Task<BenchmarkRunRecord> ToRecordWithJudgeAsync(BenchmarkRun run, CancellationToken cancellationToken)
    {
        var gates = await LoadReviewGatesAsync([run.Id], cancellationToken);
        var review = await LoadReviewAsync(run.Id, cancellationToken);
        return ToRecordWithJudge(run, await LoadJudgeViewsAsync([run.Id], cancellationToken), gates.GetValueOrDefault(run.Id)) with
        {
            ReviewState = review?.State,
            ReviewPolicyCurrent = await LoadReviewPolicyCurrentAsync(run.ProjectId, review, cancellationToken),
            Review = review,
            Artifacts = await GetRunArtifactsAsync(run.Id, cancellationToken)
        };
    }

    /// <summary>
    ///     The same record projection against views already loaded, so a batch can read every run's judge state in one
    ///     query and still produce records identical to the single-run path.
    /// </summary>
    private static BenchmarkRunRecord ToRecordWithJudge(BenchmarkRun run, IReadOnlyDictionary<Guid, BenchmarkRunJudgeView> views, ReviewGate review)
    {
        // The write paths that project a run they have just touched compare it against nothing: item edits are refused while any of the project's work is Queued or
        // Running, so a row a write just moved cannot be stale. The ranking read and the single-run read do the real comparison.
        var (judge, qualityScore, qualityScoreSource, _) = ApplyRunExclusions(JudgeViewFor(views, run.Id, run.UserScore),
            run.UserScore,
            run.IsWarmup,
            run.PrimaryStopReason,
            BenchmarkRunIdentity.Unstamped,
            review);
        return ToRecord(run) with
        {
            Judge = judge,
            QualityScore = qualityScore,
            QualityScoreSource = qualityScoreSource
        };
    }

    /// <summary>
    ///     Each run's place in its project's review phase, from flat columns only. A run absent from the result is <see cref="ReviewGate.NotApplicable" />.
    /// </summary>
    /// <remarks>
    ///     The gate applies to succeeded scoring runs of a review project: a leaf that counts toward the score, or no item at all. A decision under a revision
    ///     other than the project's current one (both NULL when there is no policy) is stale, whatever it decided.
    /// </remarks>
    private async Task<IReadOnlyDictionary<Guid, ReviewGate>> LoadReviewGatesAsync(IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return new Dictionary<Guid, ReviewGate>();
        }

        var rows = await (from run in _dbContext.BenchmarkRuns.AsNoTracking()
            join project in _dbContext.BenchmarkProjects.AsNoTracking() on run.ProjectId equals project.Id
            where runIds.Contains(run.Id)
                  && project.RequiresReview
                  && run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                  && (run.TaskItemId == null
                      || _dbContext.BenchmarkTaskItems.Any(item => item.Id == run.TaskItemId
                                                                   && item.Kind != BenchmarkTaskItemKinds.Niah
                                                                   && item.CountsTowardScore))
            join review in _dbContext.BenchmarkRunReviews.AsNoTracking() on run.Id equals review.RunId into reviews
            from review in reviews.DefaultIfEmpty()
            select new
            {
                run.Id,
                ReviewState = review == null ? null : review.State,
                Stale = review != null && review.PolicyRevisionId != project.CurrentJudgePolicyRevisionId,
                JudgingCurrent = project.CurrentJudgePolicyRevisionId == null
                                 || _dbContext.BenchmarkJudgeAttempts.Any(attempt => attempt.Id == run.CurrentJudgeAttemptId
                                                                                     && attempt.Status == BenchmarkJudgeAttemptStatus.Succeeded
                                                                                     && attempt.PolicyRevisionId == project.CurrentJudgePolicyRevisionId)
            }).ToArrayAsync(cancellationToken);

        // The same rule the review inbox and the review service apply: an open review waits for its judging to succeed under the current revision first.
        return rows.ToDictionary(static row => row.Id,
            static row => row switch
            {
                { ReviewState: null, JudgingCurrent: false } or { Stale: true, JudgingCurrent: false } => ReviewGate.AwaitingJudge,
                { ReviewState: null } => ReviewGate.Pending,
                { Stale: true } => ReviewGate.Stale,
                { ReviewState: BenchmarkReviewStates.Approved } => ReviewGate.Approved,
                _ => ReviewGate.Rejected
            });
    }

    /// <summary>
    ///     What one run was asked, against what the project asks now. Two tiny plaintext reads; a run that names no
    ///     item short-circuits to <see cref="BenchmarkRunIdentity.Unstamped" /> and cannot be stale on either axis.
    /// </summary>
    private async Task<BenchmarkRunIdentity> LoadRunIdentityAsync(BenchmarkRun entity, CancellationToken cancellationToken)
    {
        if (entity.TaskItemId is not { } itemId)
        {
            return BenchmarkRunIdentity.Unstamped;
        }

        var currentInputHash = await _dbContext.BenchmarkTaskItems.AsNoTracking()
                                               .Where(item => item.Id == itemId)
                                               .Select(static item => item.InputHash)
                                               .FirstOrDefaultAsync(cancellationToken);
        var currentSetHash = await _dbContext.BenchmarkProjects.AsNoTracking()
                                             .Where(project => project.Id == entity.ProjectId)
                                             .Select(static project => project.TaskItemSetHash)
                                             .FirstOrDefaultAsync(cancellationToken);
        return new BenchmarkRunIdentity
        {
            TaskInputHash = entity.TaskInputHash,
            CurrentInputHash = currentInputHash,
            TaskItemSetHash = entity.TaskItemSetHash,
            CurrentItemSetHash = currentSetHash
        };
    }

    /// <summary>
    ///     The project's ranking, computed once per request from flat columns only: dense rank, descending, ties
    ///     sharing a rank.
    /// </summary>
    /// <remarks>
    ///     Recomputed per request rather than maintained as a rollup: a project's run count stays small. The unit that
    ///     ranks is a CELL — one model, one KV type, one repeat of the whole task-item suite — and it ranks only when
    ///     every scorable item in it produced a rankable score. Partial credit is rejected: it would let a model that
    ///     ran out of budget on the hardest item outrank one that attempted everything, the same reason a truncated run
    ///     is excluded outright rather than scored low. A single-item project has one run per cell.
    /// </remarks>
    private async Task<BenchmarkProjectRanking> LoadRankingAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var scored = await _dbContext.BenchmarkRuns.AsNoTracking()
                                     .Where(entity => entity.ProjectId == projectId)
                                     .Select(entity => new
                                     {
                                         entity.Id,
                                         entity.UserScore,
                                         entity.PrimaryStopReason,
                                         entity.IsWarmup,
                                         entity.TaskItemId,
                                         entity.CellKey,
                                         entity.TaskInputHash,
                                         entity.TaskItemSetHash
                                     })
                                     .ToArrayAsync(cancellationToken);

        // Plaintext on both sides — hashes and flags, never a payload — so this read still decrypts nothing.
        var items = await _dbContext.BenchmarkTaskItems.AsNoTracking()
                                    .Where(entity => entity.ProjectId == projectId)
                                    .Select(entity => new
                                    {
                                        entity.Id,
                                        entity.Kind,
                                        entity.InputHash,
                                        entity.CountsTowardScore
                                    })
                                    .ToArrayAsync(cancellationToken);
        var currentInputHashes = items.ToDictionary(static item => item.Id, static item => item.InputHash);
        var scorableItemIds = items.Where(static item => BenchmarkTaskItemKinds.IsLeaf(item.Kind) && item.CountsTowardScore)
                                   .Select(static item => item.Id)
                                   .ToHashSet();
        var currentSetHash = await _dbContext.BenchmarkProjects.AsNoTracking()
                                             .Where(entity => entity.Id == projectId)
                                             .Select(static entity => entity.TaskItemSetHash)
                                             .FirstOrDefaultAsync(cancellationToken);

        var views = await LoadJudgeViewsAsync([.. scored.Select(static run => run.Id)], cancellationToken);
        var gates = await LoadReviewGatesAsync([.. scored.Select(static run => run.Id)], cancellationToken);
        var current = await GetCurrentJudgePolicyRevisionAsync(projectId, cancellationToken);
        var pairwise = await LoadPairwiseRankingAsync(current, cancellationToken);

        var runs = new Dictionary<Guid, BenchmarkRunRanking>(scored.Length);
        var rankable = new Dictionary<Guid, bool>(scored.Length);
        var anyScore = new Dictionary<Guid, bool>(scored.Length);
        var setRevised = new HashSet<Guid>();
        foreach (var run in scored)
        {
            var identity = new BenchmarkRunIdentity
            {
                TaskInputHash = run.TaskInputHash,
                CurrentInputHash = run.TaskItemId is { } itemId && currentInputHashes.TryGetValue(itemId, out var hash) ? hash : null,
                TaskItemSetHash = run.TaskItemSetHash,
                CurrentItemSetHash = currentSetHash
            };
            var (judge, qualityScore, source, runRankable) = ApplyRunExclusions(JudgeViewFor(views, run.Id, run.UserScore),
                run.UserScore,
                run.IsWarmup,
                run.PrimaryStopReason,
                identity,
                gates.GetValueOrDefault(run.Id),
                ToPairwiseRunView(pairwise, run.Id));

            rankable[run.Id] = runRankable;

            // A gated run can only ever rank on its review, so a judge score must not make its cell look scored.
            anyScore[run.Id] = gates.GetValueOrDefault(run.Id) == ReviewGate.NotApplicable
                ? run.UserScore is not null || judge.Score is not null || PairwiseScoreFor(pairwise, run.Id)?.Score is not null
                : run.UserScore is not null;
            if (identity.SetRevised)
            {
                _ = setRevised.Add(run.Id);
            }

            runs[run.Id] = new BenchmarkRunRanking
            {
                Judge = judge,
                QualityScore = qualityScore,
                Source = source,
                Rank = null,
                CellQuality = null
            };
        }

        // An open review closes the gap, including on a truncated answer: an approval's user score outranks truncation. A stale identity stamp it cannot close.
        bool IsReviewGap(Guid runId) =>
            gates.GetValueOrDefault(runId) is ReviewGate.Pending or ReviewGate.Stale
            && runs[runId].Judge.RankExclusionReason is BenchmarkRunJudgeStates.ReasonReviewPending
                or BenchmarkRunJudgeStates.ReasonReviewStale
                or BenchmarkRunJudgeStates.ReasonTruncated
                or BenchmarkRunJudgeStates.ReasonIncomplete;

        // Warm-ups are dropped BEFORE grouping: one sits at repeat index 0, so it forms a cell of its own that could only ever be complete if every leaf item also
        // got a warm-up run, and would then sit in the denominator forever. Not a contender at cell granularity, for the reason it is not one at run granularity.
        var cells = new Dictionary<string, CellRanking>(StringComparer.Ordinal);
        foreach (var cell in scored.Where(static run => !run.IsWarmup).GroupBy(static run => run.CellKey, StringComparer.Ordinal))
        {
            var members = cell.ToArray();

            // The set check comes FIRST: "does this cell hold every current item" is only a meaningful question once
            // both sides agree on what the set is. Asking it of a cell frozen under a different set is the bug.
            if (Array.Exists(members, member => setRevised.Contains(member.Id)))
            {
                cells[cell.Key] = new CellRanking
                {
                    Quality = null,
                    Reason = BenchmarkRunJudgeStates.ReasonItemSetRevised,
                    Countable = false
                };
                continue;
            }

            // A cell whose runs name no item at all was frozen before task suites: it is a singleton, it is ranked on
            // its own run's score, and materializing item 0 for the project later must not retroactively unrank it.
            var contributing = members.Where(member => member.TaskItemId is { } id && scorableItemIds.Contains(id)).ToArray();
            if (contributing.Length == 0 && Array.TrueForAll(members, static member => member.TaskItemId is null))
            {
                var only = members[0];
                cells[cell.Key] = new CellRanking
                {
                    Quality = runs[only.Id].QualityScore,
                    Reason = runs[only.Id].QualityScore is null ? runs[only.Id].Judge.RankExclusionReason : null,
                    Countable = rankable[only.Id] && anyScore[only.Id]
                };
                continue;
            }

            // A project whose every leaf is excluded from the mean — a pure long-context probe, where recall is reported on its own axis — has nothing to rank, which
            // is not a cell missing an item: "incomplete" would send the operator hunting a question never asked. The runs keep their own scores for the recall axis.
            if (scorableItemIds.Count == 0)
            {
                cells[cell.Key] = new CellRanking
                {
                    Quality = null,
                    Reason = BenchmarkRunJudgeStates.ReasonNoScore,
                    Countable = false
                };
                continue;
            }

            var covered = contributing.Select(static member => member.TaskItemId!.Value).ToHashSet();
            var complete = scorableItemIds.All(covered.Contains)
                           && Array.TrueForAll(contributing, member => rankable[member.Id]);
            if (!complete)
            {
                // Every item answered and every gap a review the operator still owes: the inbox closes it, a re-run would not. A missing item stays incomplete.
                var reviewGapsOnly = scorableItemIds.All(covered.Contains)
                                     && Array.TrueForAll(contributing, member => (rankable[member.Id] && runs[member.Id].QualityScore is not null) || IsReviewGap(member.Id));
                cells[cell.Key] = new CellRanking
                {
                    Quality = null,
                    Reason = reviewGapsOnly ? BenchmarkRunJudgeStates.ReasonReviewPending : BenchmarkRunJudgeStates.ReasonItemIncomplete,
                    Countable = false
                };
                continue;
            }

            var quality = Array.TrueForAll(contributing, member => runs[member.Id].QualityScore is not null)
                ? CellMean(contributing.Select(member => runs[member.Id].QualityScore!.Value))
                : (int?)null;
            cells[cell.Key] = new CellRanking
            {
                Quality = quality,
                Reason = quality is null ? BenchmarkRunJudgeStates.ReasonItemIncomplete : null,
                Countable = Array.TrueForAll(contributing, member => anyScore[member.Id])
            };
        }

        // Dense rank over CELLS: equal scores share a position and the next distinct score is the next integer, so "rank 2" is always "the second-best score in this
        // project", however many cells tie above it. Every run of a cell reports its cell's rank and its cell's mean; its own quality score stays its own.
        var ordered = cells.Values.Where(static entry => entry.Quality is not null)
                           .Select(static entry => entry.Quality!.Value)
                           .Distinct()
                           .OrderByDescending(static score => score)
                           .ToArray();
        foreach (var run in scored)
        {
            if (run.IsWarmup || !cells.TryGetValue(run.CellKey, out var cell))
            {
                continue;
            }

            var entry = runs[run.Id];
            runs[run.Id] = entry with
            {
                CellQuality = cell.Quality,
                Rank = cell.Quality is { } quality ? Array.IndexOf(ordered, quality) + 1 : null,

                // A run the cell excluded but that nothing about the run itself excluded reports the cell's reason:
                // "your sibling item never answered" is why this row does not rank, and it is not visible anywhere else.
                Judge = entry.Judge.RankExclusionReason is null && cell.Reason is not null
                    ? entry.Judge with
                    {
                        RankExclusionReason = cell.Reason
                    }
                    : entry.Judge
            };
        }

        return new BenchmarkProjectRanking
        {
            Runs = runs,
            Cohort = new BenchmarkRankCohort
            {
                PolicyRevision = current?.Revision,
                ExecutionKey = current?.ReferenceExecutionKey,
                CohortGeneration = current?.CohortGeneration,
                RankedCount = cells.Values.Count(static entry => entry.Quality is not null),
                TotalScored = cells.Values.Count(static entry => entry.Countable)
            },
            Cells = cells,
            ScorableItemCount = scorableItemIds.Count
        };
    }

    private static BenchmarkRunRecord WithRanking(BenchmarkRunRecord run, BenchmarkProjectRanking ranking) =>
        ranking.Runs.TryGetValue(run.Id, out var entry)
            ? run with
            {
                Judge = entry.Judge,
                QualityScore = entry.QualityScore,
                QualityScoreSource = entry.Source,
                Rank = entry.Rank,
                CellQuality = entry.CellQuality
            }
            : run;

    /// <summary>The whole project's pairwise ranking input — one parsed fit row, or the reason there is no usable one.</summary>
    private sealed record PairwiseRanking
    {
        public required IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> Scores { get; init; }

        public required string? ScopeReason { get; init; }
    }

    /// <summary>
    ///     The project's pairwise ranking input, or <see langword="null" /> when it does not judge pairwise.
    /// </summary>
    /// <remarks>
    ///     Pairwise is read off the revision's <c>ComparisonSetVersion</c> (bumped by its first comparison, never back to zero; a mode
    ///     switch mints a revision at zero) or its active fit, which a no-pair cohort's refusal publishes at zero; no policy blob is
    ///     decrypted per fetch. Staleness is the fit's stored version against the revision's plus the promoted execution key.
    /// </remarks>
    private async Task<PairwiseRanking?> LoadPairwiseRankingAsync(BenchmarkJudgePolicyRevisionRecord? current, CancellationToken cancellationToken)
    {
        if (current is null)
        {
            return null;
        }

        // Version 0 with an active fit is the comparison-free refusal (one model with repeats): the planner formed no pair, yet the project judges pairwise.
        var fit = await ActiveFitAsync(current.Id, current.CohortGeneration, cancellationToken);
        if (fit is null && current.ComparisonSetVersion == 0)
        {
            return null;
        }

        if (fit is null)
        {
            return new PairwiseRanking
            {
                Scores = new Dictionary<Guid, BenchmarkPairwiseScoreEntry>(),
                ScopeReason = BenchmarkRunJudgeStates.ReasonPairwisePending
            };
        }

        if (fit.ComparisonSetVersion != current.ComparisonSetVersion
            || !string.Equals(fit.JudgeExecutionKey, current.ReferenceExecutionKey ?? string.Empty, StringComparison.Ordinal))
        {
            return new PairwiseRanking
            {
                Scores = new Dictionary<Guid, BenchmarkPairwiseScoreEntry>(),
                ScopeReason = BenchmarkRunJudgeStates.ReasonPairwiseStale
            };
        }

        // Ranked as if no fit were published when the blob is unreadable, the same rule every other ScoresJson reader applies.
        var entries = BenchmarkPairwiseFitScores.TryRead(fit, _logger);
        if (entries is null)
        {
            return new PairwiseRanking
            {
                Scores = new Dictionary<Guid, BenchmarkPairwiseScoreEntry>(),
                ScopeReason = BenchmarkRunJudgeStates.ReasonPairwisePending
            };
        }

        return new PairwiseRanking
        {
            Scores = entries.ToDictionary(static entry => entry.RunId),
            ScopeReason = null
        };
    }

    private static BenchmarkPairwiseScoreEntry? PairwiseScoreFor(PairwiseRanking? pairwise, Guid runId) =>
        pairwise is not null && pairwise.Scores.TryGetValue(runId, out var entry) ? entry : null;

    /// <summary>
    ///     A run's fit view. A run the fit does not mention at all is <c>pairwise-insufficient</c>: it was eligible
    ///     when the ranking read ran but was not in the set the fit covered.
    /// </summary>
    private static PairwiseRunView? ToPairwiseRunView(PairwiseRanking? pairwise, Guid runId)
    {
        if (pairwise is null)
        {
            return null;
        }

        var entry = PairwiseScoreFor(pairwise, runId);
        return new PairwiseRunView
        {
            Score = entry?.Score,
            Reason = entry?.Reason ?? pairwise.ScopeReason ?? (entry is null ? BenchmarkRunJudgeStates.ReasonPairwiseInsufficient : null)
        };
    }

    private sealed record BenchmarkRunRanking
    {
        public required BenchmarkRunJudgeView Judge { get; init; }

        public required int? QualityScore { get; init; }

        public required string Source { get; init; }

        public required int? Rank { get; init; }

        public required int? CellQuality { get; init; }
    }

    /// <summary>
    ///     One measurement cell: the mean of its scorable items' qualities, or the reason it has none.
    /// </summary>
    private sealed record CellRanking
    {
        public required int? Quality { get; init; }

        public required string? Reason { get; init; }

        /// <summary>
        ///     Whether this cell belongs in the "n of m ranked" denominator — complete, every member rankable, every member
        ///     carrying some score. A cell nothing the operator does could ever rank must not sit in it.
        /// </summary>
        public required bool Countable { get; init; }
    }

    private sealed record BenchmarkProjectRanking
    {
        public required IReadOnlyDictionary<Guid, BenchmarkRunRanking> Runs { get; init; }

        public required BenchmarkRankCohort Cohort { get; init; }

        public required IReadOnlyDictionary<string, CellRanking> Cells { get; init; }

        public required int ScorableItemCount { get; init; }
    }

    private static BenchmarkRunJudgeView JudgeViewFor(IReadOnlyDictionary<Guid, BenchmarkRunJudgeView> views, Guid runId, int? userScore)
    {
        if (views.TryGetValue(runId, out var view))
        {
            return view;
        }

        // No attempt: there is nothing to derive a judging from, so the run is unranked unless the operator scored it.
        var reason = userScore is null ? BenchmarkRunJudgeStates.ReasonNoScore : null;
        return new BenchmarkRunJudgeView
        {
            State = BenchmarkRunJudgeStates.None,
            AttemptId = null,
            Score = null,
            PolicyRevision = null,
            PolicyRevisionId = null,
            AttemptSequence = null,
            CohortGeneration = null,
            ExecutionKey = null,
            ErrorMessage = null,
            PolicyCurrent = false,
            ExecutionCurrent = false,
            RankExclusionReason = reason
        };
    }

    /// <summary>
    ///     The derived judge state for each run that has a current attempt. Runs without one are absent, and the caller
    ///     substitutes the <c>none</c> view — a run with no attempt has nothing to derive from.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, BenchmarkRunJudgeView>> LoadJudgeViewsAsync(IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0)
        {
            return new Dictionary<Guid, BenchmarkRunJudgeView>();
        }

        // Flat columns only across all four tables, so nothing is decrypted to answer "is this run ranked?".
        var rows = await (from run in _dbContext.BenchmarkRuns.AsNoTracking()
            join attempt in _dbContext.BenchmarkJudgeAttempts.AsNoTracking() on run.CurrentJudgeAttemptId equals attempt.Id
            join revision in _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking() on attempt.PolicyRevisionId equals revision.Id
            join project in _dbContext.BenchmarkProjects.AsNoTracking() on run.ProjectId equals project.Id
            where runIds.Contains(run.Id)
            select new JudgeViewRow
            {
                RunId = run.Id,
                AttemptId = attempt.Id,
                UserScore = run.UserScore,
                Status = attempt.Status,
                Score = attempt.Score,
                Sequence = attempt.Sequence,
                AttemptGeneration = attempt.CohortGeneration,
                ExecutionKey = attempt.JudgeExecutionKey,
                ErrorMessage = attempt.ErrorMessage,
                PolicyRevisionId = attempt.PolicyRevisionId,
                RevisionNumber = revision.Revision,
                RevisionGeneration = revision.CohortGeneration,
                ReferenceExecutionKey = revision.ReferenceExecutionKey,
                ProjectCurrentRevisionId = project.CurrentJudgePolicyRevisionId
            }).ToArrayAsync(cancellationToken);
        return rows.ToDictionary(static row => row.RunId, BuildJudgeView);
    }

    /// <summary>
    ///     Rank membership is decided at read time: an operator score always ranks, a judge score only under the
    ///     project's current policy revision.
    /// </summary>
    /// <remarks>
    ///     A judge score must also sit in that revision's live cohort generation, with the execution key the cohort was
    ///     claimed with. Anything else is honestly unranked, with a reason the UI can act on.
    /// </remarks>
    private static BenchmarkRunJudgeView BuildJudgeView(JudgeViewRow row)
    {
        var state = row.Status switch
        {
            BenchmarkJudgeAttemptStatus.Queued => BenchmarkRunJudgeStates.Queued,
            BenchmarkJudgeAttemptStatus.Running => BenchmarkRunJudgeStates.Running,
            BenchmarkJudgeAttemptStatus.Succeeded => BenchmarkRunJudgeStates.Succeeded,
            BenchmarkJudgeAttemptStatus.Failed => BenchmarkRunJudgeStates.Failed,
            _ => BenchmarkRunJudgeStates.Cancelled
        };
        var policyCurrent = row.ProjectCurrentRevisionId == row.PolicyRevisionId;
        var executionCurrent = row.AttemptGeneration == row.RevisionGeneration
                               && row.ExecutionKey is not null
                               && string.Equals(row.ExecutionKey, row.ReferenceExecutionKey, StringComparison.Ordinal);
        return new BenchmarkRunJudgeView
        {
            State = state,
            AttemptId = row.AttemptId,
            Score = row.Score,
            PolicyRevision = row.RevisionNumber,
            PolicyRevisionId = row.PolicyRevisionId,
            AttemptSequence = row.Sequence,
            CohortGeneration = row.AttemptGeneration,
            ExecutionKey = row.ExecutionKey,
            ErrorMessage = row.ErrorMessage,
            PolicyCurrent = policyCurrent,
            ExecutionCurrent = executionCurrent,
            RankExclusionReason = RankExclusionReason(row, policyCurrent, executionCurrent)
        };
    }

    private static string? RankExclusionReason(JudgeViewRow row, bool policyCurrent, bool executionCurrent)
    {
        if (row.UserScore is not null)
        {
            return null;
        }

        return row.Status switch
        {
            BenchmarkJudgeAttemptStatus.Queued or BenchmarkJudgeAttemptStatus.Running => BenchmarkRunJudgeStates.ReasonJudgePending,
            // A judging that failed because a verifier could not RUN is not the same fact as one whose judge model failed: unranked either way, but only one is fixed
            // by an operator action on the node. Nor is one refused because the item's override named a criterion the rubric lacks — that is fixed by editing either.
            BenchmarkJudgeAttemptStatus.Failed => FailedReason(row.ErrorMessage),
            BenchmarkJudgeAttemptStatus.Cancelled => BenchmarkRunJudgeStates.ReasonJudgeCancelled,
            _ when row.Score is null => BenchmarkRunJudgeStates.ReasonNoScore,
            _ when !policyCurrent => BenchmarkRunJudgeStates.ReasonPolicyOutdated,
            _ when row.AttemptGeneration != row.RevisionGeneration => BenchmarkRunJudgeStates.ReasonGenerationStale,
            _ when row.ExecutionKey is null => BenchmarkRunJudgeStates.ReasonExecutionIdentityIncomplete,
            _ when !executionCurrent => BenchmarkRunJudgeStates.ReasonExecutionKeyMismatch,
            _ => null
        };
    }

    /// <summary>
    ///     Which failure a failed judging was. The message prefix is the only column carrying it: the executor is the
    ///     one thing that writes either prefix, and it writes each only for the condition it names.
    /// </summary>
    private static string FailedReason(string? errorMessage) =>
        errorMessage switch
        {
            not null when errorMessage.StartsWith(BenchmarkRunJudgeStates.VerifierUnavailablePrefix, StringComparison.Ordinal) =>
                BenchmarkRunJudgeStates.ReasonVerifierUnavailable,
            not null when errorMessage.StartsWith(BenchmarkRunJudgeStates.OverrideUnmatchedPrefix, StringComparison.Ordinal) =>
                BenchmarkRunJudgeStates.ReasonOverrideUnmatched,
            _ => BenchmarkRunJudgeStates.ReasonJudgeFailed
        };

    /// <summary>The flat columns the derived judge view is computed from. Never leaves this class.</summary>
    private sealed record JudgeViewRow
    {
        public required Guid RunId { get; init; }

        public required Guid AttemptId { get; init; }

        public required int? UserScore { get; init; }

        public required BenchmarkJudgeAttemptStatus Status { get; init; }

        public required int? Score { get; init; }

        public required int Sequence { get; init; }

        public required int AttemptGeneration { get; init; }

        public required string? ExecutionKey { get; init; }

        public required string? ErrorMessage { get; init; }

        public required Guid PolicyRevisionId { get; init; }

        public required int RevisionNumber { get; init; }

        public required int RevisionGeneration { get; init; }

        public required string? ReferenceExecutionKey { get; init; }

        public required Guid? ProjectCurrentRevisionId { get; init; }
    }
}
