namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One immutable judging policy of a project. A project points at exactly one of these at a time; every judge
///     attempt names the revision it was judged under, so a rubric change never silently re-labels an old score.
/// </summary>
internal sealed record class BenchmarkJudgePolicyRevision
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>1..n within the project, in activation order. Never reused, never renumbered.</summary>
    public int Revision { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_judge_policy_json</c>.
    /// </summary>
    public byte[] PolicyJson { get; set; } = [];

    /// <summary>Lowercase hex SHA-256 over the canonical policy JSON. Plaintext, and unique within the project.</summary>
    public string PolicyHash { get; set; } = string.Empty;

    /// <summary>
    ///     The judge execution key the ranked cohort is defined by, promoted once per cohort generation by the first
    ///     same-generation attempt that succeeds. NULL means the cohort is open and nothing has claimed it yet.
    /// </summary>
    public string? ReferenceExecutionKey { get; set; }

    /// <summary>
    ///     Bumped whenever the cohort is reset (any activation of this revision). An attempt stamped with an older
    ///     generation can never promote the reference key and is never part of the current cohort.
    /// </summary>
    public int CohortGeneration { get; set; }

    public long CreatedAtUtc { get; set; }
}
