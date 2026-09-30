namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The benchmark scoring policy, pure: which run a score may rank, which score that is, and how a cell averages.
/// </summary>
/// <remarks>
///     A product rule, not query shaping: operator score &gt; pairwise fit &gt; judge, the stale-identity, warm-up and
///     truncation exclusions and the cell rounding. It lives beside <see cref="BenchmarkStore" /> only because the store's
///     batched ranking read applies it and Persistence may reference nothing above Abstractions; it reads no rows.
/// </remarks>
internal static class BenchmarkRankingPolicy
{
    /// <summary>
    ///     The two RUN-level exclusions — those that come from the run itself rather than from its judging — plus the resulting quality score.
    /// </summary>
    /// <remarks>
    ///     Every path that hands back a run record routes through here, so the single-run read and the write-returning paths cannot report a judge-derived
    ///     <c>no-score</c> on a run whose only problem is truncation. Outermost first: a WARM-UP outranks even the operator override, since ranking it would rank
    ///     the first-launch cost it controls for. TRUNCATION and the SILENT-INCOMPLETE beside it follow, before every judge-derived reason and after the override —
    ///     their score stays visible but never ranks, read off the persisted stop reason, not the status.
    /// </remarks>
    /// <param name="pairwise">
    ///     This run's place in the project's active pairwise fit, or <see langword="null" /> when judging is pointwise;
    ///     there no judge attempt exists and the fit alone decides the exclusion.
    /// </param>
    /// <returns><c>Rankable</c> says whether a score could ever rank this run, so the ranking's denominator cannot drift.</returns>
    public static (BenchmarkRunJudgeView Judge, int? QualityScore, string Source, bool Rankable) ApplyRunExclusions(BenchmarkRunJudgeView judge,
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
    public static string? StaleReason(bool revised, bool setRevised)
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
    public sealed record BenchmarkRunIdentity
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

        public bool SetRevised => TaskItemSetHash is not null && !string.Equals(TaskItemSetHash, CurrentItemSetHash ?? BenchmarkStore.LegacyTaskHash, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The exclusion a stop reason implies for a run nothing else already excludes, or <see langword="null" /> when
    ///     the run answered.
    /// </summary>
    public static string? UnansweredReason(string? primaryStopReason)
    {
        if (BenchmarkPrimaryStopReasons.IsTruncated(primaryStopReason))
        {
            return BenchmarkRunJudgeStates.ReasonTruncated;
        }

        return BenchmarkPrimaryStopReasons.IsIncomplete(primaryStopReason) ? BenchmarkRunJudgeStates.ReasonIncomplete : null;
    }

    /// <summary>
    ///     The run's ranking value: the operator's override when set, otherwise the judge score, but only while that
    ///     judging is in the project's current cohort.
    /// </summary>
    /// <remarks>
    ///     A score from an outdated policy or a different judge runtime is still shown, it just does not rank.
    /// </remarks>
    public static (int? QualityScore, string Source) ComputeQuality(int? userScore, BenchmarkRunJudgeView judge, PairwiseRunView? pairwise = null)
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
    public sealed record PairwiseRunView
    {
        public required int? Score { get; init; }

        public required string? Reason { get; init; }
    }

    /// <summary>A complete cell's quality: the mean of its contributing runs' scores, rounded half away from zero.</summary>
    public static int CellMean(IEnumerable<int> qualities) =>
        (int)Math.Round(qualities.Average(static quality => (double)quality), MidpointRounding.AwayFromZero);
}
