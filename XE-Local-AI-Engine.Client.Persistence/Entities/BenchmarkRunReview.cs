namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A reviewer's decision on one run of a project that requires review. A row exists only once someone decided:
///     pending is derived from its absence (or from a decision under a superseded policy revision), never stored.
/// </summary>
internal sealed record class BenchmarkRunReview
{
    public Guid Id { get; set; }

    /// <summary>One decision per run; a re-submission overwrites it under the run-version compare-and-swap.</summary>
    public Guid RunId { get; set; }

    /// <summary><c>approved</c> or <c>rejected</c> (<c>BenchmarkReviewStates</c>).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The 0..100 overall score an approval wrote onto the run's user score; NULL when rejected.</summary>
    public int? Score { get; set; }

    /// <summary>Plaintext <c>[{criterionId, score}]</c>, 0..10 each. Numbers and rubric ids only.</summary>
    public string? CriteriaScoresJson { get; set; }

    /// <summary>
    ///     The judge-policy revision the decision was made under, or NULL for a project with no policy. Plaintext so
    ///     staleness is a flat comparison against the project's current revision pointer.
    /// </summary>
    public Guid? PolicyRevisionId { get; set; }

    /// <summary>
    ///     Plaintext UTF-8 JSON while tracked; encrypted at rest with node-scoped AAD column
    ///     <c>benchmark_run_review_notes_json</c>. Free text that may quote the answer.
    /// </summary>
    public byte[]? NotesJson { get; set; }

    public long Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
