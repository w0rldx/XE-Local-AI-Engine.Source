namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One Bradley–Terry fit over one cohort's pairwise verdicts, stored as ONE immutable row with ONE active pointer
///     per <c>(revision, generation, case)</c>. A fit is a single object: publishing it by writing a score onto each
///     run would let a crash leave a ranking that blends two fits, with every row internally consistent and the
///     ordering wrong. Ranking reads the scores out of this row, so that state is not reachable.
/// </summary>
internal sealed record class BenchmarkPairwiseFit
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PolicyRevisionId { get; set; }
    public int CohortGeneration { get; set; }

    /// <summary>The task case this fit covers. Null for a legacy single-case fit; Bradley–Terry never crosses cases.</summary>
    public Guid? TaskCaseId { get; set; }

    /// <summary>
    ///     <c>v1:</c> + 64 hex over the fit's whole identity: revision, generation, policy hash, both pairwise
    ///     versions, the case, the promoted judge execution key and the comparison-set version. UNIQUE, so a duplicate
    ///     publication violates and no-ops rather than minting a second fit of the same thing.
    /// </summary>
    public string FitKey { get; set; } = string.Empty;

    /// <summary>
    ///     The promoted <c>ReferenceExecutionKey</c> every fitted comparison carried. NOT NULL: a fit over comparisons
    ///     with no execution identity is refused, not stored.
    /// </summary>
    public string JudgeExecutionKey { get; set; } = string.Empty;

    /// <summary>
    ///     The revision's <c>ComparisonSetVersion</c> at fit time. Staleness is this integer against the revision's
    ///     current value — one comparison against a row the ranking read already loads, no comparison rows read.
    /// </summary>
    public int ComparisonSetVersion { get; set; }

    /// <summary>
    ///     Plaintext canonical JSON of the ordered <c>(runAId, runBId, order, verdict)</c> tuples actually fitted —
    ///     the auditable answer to "which verdicts produced this number". Evidence, never re-hashed on a read path.
    /// </summary>
    public string FittedSetJson { get; set; } = string.Empty;

    /// <summary>
    ///     Plaintext canonical JSON <c>[{runId, score, ciLow, ciHigh, comparisons, bootstrapAppearances}]</c>.
    ///     Plaintext because it is the rank input, exactly the posture of <see cref="BenchmarkJudgeAttempt.Score" />.
    /// </summary>
    public string ScoresJson { get; set; } = string.Empty;

    /// <summary>MM sweeps used, under the iteration cap.</summary>
    public int Iterations { get; set; }

    public int BootstrapReplicates { get; set; }

    /// <summary>At most one true per scope, enforced by a filtered unique index rather than by publisher care.</summary>
    public bool IsActive { get; set; }

    public long CreatedAtUtc { get; set; }
    public long Version { get; set; }
}
