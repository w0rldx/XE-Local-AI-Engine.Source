namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    private async Task<BenchmarkRunRecord> ToRecordWithJudgeAsync(BenchmarkRun run, CancellationToken cancellationToken)
    {
        return ToRecordWithJudge(run, await LoadJudgeViewsAsync([run.Id], cancellationToken));
    }

    /// <summary>
    ///     The same record projection against views already loaded, so a batch can read every run's judge state in one
    ///     query and still produce records identical to the single-run path.
    /// </summary>
    private static BenchmarkRunRecord ToRecordWithJudge(BenchmarkRun run, IReadOnlyDictionary<Guid, BenchmarkRunJudgeView> views)
    {
        // The write paths that project a run they have just touched compare it against nothing: item edits are refused while any of the project's work is Queued or
        // Running, so a row a write just moved cannot be stale. The ranking read and the single-run read do the real comparison.
        var (judge, qualityScore, qualityScoreSource, _) = ApplyRunExclusions(JudgeViewFor(views, run.Id, run.UserScore),
            run.UserScore,
            run.IsWarmup,
            run.PrimaryStopReason,
            BenchmarkRunIdentity.Unstamped);
        return ToRecord(run) with
        {
            Judge = judge,
            QualityScore = qualityScore,
            QualityScoreSource = qualityScoreSource
        };
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
                ToPairwiseRunView(pairwise, run.Id));

            rankable[run.Id] = runRankable;
            anyScore[run.Id] = run.UserScore is not null || judge.Score is not null || PairwiseScoreFor(pairwise, run.Id)?.Score is not null;
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
                cells[cell.Key] = new CellRanking
                {
                    Quality = null,
                    Reason = BenchmarkRunJudgeStates.ReasonItemIncomplete,
                    Countable = false
                };
                continue;
            }

            // Half away from zero, matching ComputeQuality's own arithmetic.
            var quality = Array.TrueForAll(contributing, member => runs[member.Id].QualityScore is not null)
                ? (int)Math.Round(contributing.Average(member => (double)runs[member.Id].QualityScore!.Value), MidpointRounding.AwayFromZero)
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

    /// <summary>
    ///     The two RUN-level exclusions — those that come from the run itself rather than from its judging — plus the resulting quality score.
    /// </summary>
    /// <remarks>
    ///     Every path that hands back a run record routes through here, so the single-run read and the write-returning paths cannot report a judge-derived
    ///     <c>no-score</c> on a run whose only problem is truncation. Outermost first: a WARM-UP outranks even the operator override, since ranking it would rank
    ///     the first-launch cost it controls for. TRUNCATION and the SILENT-INCOMPLETE beside it follow, before every judge-derived reason and after the override —
    ///     their score stays visible but never ranks, read off the persisted stop reason, not the status.
    /// </remarks>
    /// <param name="Rankable">Whether a score could ever rank this run, returned rather than re-derived so the ranking's denominator cannot drift.</param>
    /// <param name="pairwise">
    ///     This run's place in the project's active pairwise fit, or <see langword="null" /> when judging is pointwise;
    ///     there no judge attempt exists and the fit alone decides the exclusion.
    /// </param>
    private static (BenchmarkRunJudgeView Judge, int? QualityScore, string Source, bool Rankable) ApplyRunExclusions(BenchmarkRunJudgeView judge,
        int? userScore,
        bool isWarmup,
        string? primaryStopReason,
        BenchmarkRunIdentity identity,
        PairwiseRunView? pairwise = null)
    {
        // The stale stamps sit ABOVE the operator override, truncation still below it: an operator who read a truncated answer and scored it anyway has overruled
        // the machine about a fact they could see, while one who scored an answer to a since-edited question, or to an item of a since-changed suite, could not.
        var revised = identity.Revised;
        var setRevised = identity.SetRevised;
        var stale = revised || setRevised;
        var unanswered = isWarmup || stale || userScore is not null ? null : UnansweredReason(primaryStopReason);
        if (!isWarmup && !stale && unanswered is null)
        {
            var (score, source) = ComputeQuality(userScore, judge, pairwise);
            if (pairwise is null)
            {
                return (judge, score, source, true);
            }

            return (judge with
            {
                RankExclusionReason = userScore is null ? pairwise.Reason : null
            }, score, source, true);
        }

        // The more specific cause wins: "your question changed" before "the suite around it changed".
        var reason = StaleReason(revised, setRevised) ?? unanswered;
        return (judge with
        {
            RankExclusionReason = isWarmup ? BenchmarkRunJudgeStates.ReasonWarmup : reason
        }, null, BenchmarkQualityScoreSources.None, false);
    }

    /// <summary>
    ///     Which stale-identity reason a run carries, or <see langword="null" /> when neither stamp moved. The more
    ///     specific cause wins, so the badge names the question rather than the suite whenever both apply.
    /// </summary>
    private static string? StaleReason(bool revised, bool setRevised)
    {
        if (revised)
        {
            return BenchmarkRunJudgeStates.ReasonItemRevised;
        }

        return setRevised ? BenchmarkRunJudgeStates.ReasonItemSetRevised : null;
    }

    /// <summary>
    ///     What a run was asked, against what the project asks now. Both sides are plaintext, so the ranking read still
    ///     never decrypts anything.
    /// </summary>
    /// <remarks>
    ///     The two axes fail differently and neither implies the other. <see cref="TaskInputHash" /> answers "was this
    ///     run's own question edited"; every run of an untouched item passes it. <see cref="TaskItemSetHash" /> answers
    ///     "was this cell measured against the suite the project now claims", and catches the deletion case nothing
    ///     else does: delete the item a cell never answered and its surviving runs still match their own item hashes,
    ///     now forming a COMPLETE cell whose mean is over a suite the model was never scored on.
    /// </remarks>
    private sealed record BenchmarkRunIdentity
    {
        public required string? TaskInputHash { get; init; }

        /// <summary>
        ///     The item's hash now, or <see langword="null" /> when the run names no item (pre-suite) or names one that no
        ///     longer exists — in which case the set hash has moved and is the accurate reason.
        /// </summary>
        public required string? CurrentInputHash { get; init; }

        public required string? TaskItemSetHash { get; init; }

        public required string? CurrentItemSetHash { get; init; }

        /// <summary>A run frozen before task suites, or a projection that has no project state to compare against.</summary>
        public static BenchmarkRunIdentity Unstamped { get; } = new()
        {
            TaskInputHash = null,
            CurrentInputHash = null,
            TaskItemSetHash = null,
            CurrentItemSetHash = null
        };

        public bool Revised => CurrentInputHash is not null && !string.Equals(TaskInputHash, CurrentInputHash, StringComparison.Ordinal);

        public bool SetRevised => TaskItemSetHash is not null && !string.Equals(TaskItemSetHash, CurrentItemSetHash ?? LegacyTaskHash, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The exclusion a stop reason implies for a run nothing else already excludes, or <see langword="null" /> when
    ///     the run answered.
    /// </summary>
    private static string? UnansweredReason(string? primaryStopReason)
    {
        if (BenchmarkPrimaryStopReasons.IsTruncated(primaryStopReason))
        {
            return BenchmarkRunJudgeStates.ReasonTruncated;
        }

        return BenchmarkPrimaryStopReasons.IsIncomplete(primaryStopReason) ? BenchmarkRunJudgeStates.ReasonIncomplete : null;
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

    /// <summary>
    ///     The run's ranking value: the operator's override when set, otherwise the judge score, but only while that
    ///     judging is in the project's current cohort.
    /// </summary>
    /// <remarks>
    ///     A score from an outdated policy or a different judge runtime is still shown, it just does not rank.
    /// </remarks>
    private static (int? QualityScore, string Source) ComputeQuality(int? userScore, BenchmarkRunJudgeView judge, PairwiseRunView? pairwise = null)
    {
        if (userScore is { } operatorScore)
        {
            return (operatorScore, BenchmarkQualityScoreSources.User);
        }

        // Pairwise mode ranks through the cohort's active fit and NEVER through a judge attempt: there are no pointwise attempts in such a cohort, and a leftover
        // one from a previous revision is exactly what the fit scope exists to keep out of the ranking.
        if (pairwise is not null)
        {
            return pairwise.Score is { } fitted ? (fitted, BenchmarkQualityScoreSources.Pairwise) : (null, BenchmarkQualityScoreSources.None);
        }

        var judgeScore = judge is { State: BenchmarkRunJudgeStates.Succeeded, PolicyCurrent: true, ExecutionCurrent: true }
            ? judge.Score
            : null;
        return judgeScore is { } score
            ? (score, BenchmarkQualityScoreSources.Judge)
            : (null, BenchmarkQualityScoreSources.None);
    }

    /// <summary>One run's place in the active fit: the strength that ranks it, or the reason it has none.</summary>
    private sealed record PairwiseRunView
    {
        public required int? Score { get; init; }

        public required string? Reason { get; init; }
    }

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
    ///     Whether a project judges pairwise is read off the revision's <c>ComparisonSetVersion</c>: the first comparison
    ///     a revision enqueues bumps it, it never returns to zero, and a mode switch changes the policy hash, minting a
    ///     revision that starts at zero — so the mode question stays on a row the ranking already loads instead of
    ///     decrypting a policy blob per fetch. Staleness is the fit's stored version against the revision's current one
    ///     plus the promoted execution key; no comparison row is read, as the fit key's other inputs mint a revision.
    /// </remarks>
    private async Task<PairwiseRanking?> LoadPairwiseRankingAsync(BenchmarkJudgePolicyRevisionRecord? current, CancellationToken cancellationToken)
    {
        if (current is null || current.ComparisonSetVersion == 0)
        {
            return null;
        }

        var fit = await ActiveFitAsync(current.Id, current.CohortGeneration, cancellationToken);
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

        var entries = JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(fit.ScoresJson, PairwiseScoreOptions) ?? [];
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

    /// <summary>
    ///     Applies a project write's judge half to the tracked project: null policy disables, an unchanged hash is a
    ///     no-op, anything else get-or-creates the revision, resets its cohort and repoints the project.
    /// </summary>
    /// <returns>
    ///     The revision the project ends up on, or <see langword="null" /> when judging was turned off.
    /// </returns>
}
