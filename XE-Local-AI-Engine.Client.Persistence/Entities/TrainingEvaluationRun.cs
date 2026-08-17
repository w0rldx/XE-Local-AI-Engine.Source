namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One model scored against a frozen hold-out membership. An evaluation is created FROM a training run — the base
///     model and the tuned model are each evaluated against the SAME membership that run froze, which is what makes the
///     two sides of a comparison comparable at all.
/// </summary>
internal sealed record class TrainingEvaluationRun
{
    public Guid Id { get; set; }

    /// <summary>
    ///     The run whose freeze this evaluation borrowed its membership from. Nullable because the column can carry an
    ///     evaluation created outside the wizard; the wizard path always sets it. Indexed.
    /// </summary>
    public Guid? TrainingRunId { get; set; }

    /// <summary>The report this evaluation is bound into, once one exists. Null while unbound; the delete guard reads it.</summary>
    public Guid? ComparisonId { get; set; }

    /// <summary>The registry name of the model that was scored. Plaintext — it is what the report names.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Content fingerprint of <see cref="ModelName" /> at evaluation time. Lineage only, so nullable.</summary>
    public string? ModelContentFingerprint { get; set; }

    public EvaluationModelTargetKind TargetKind { get; set; } = EvaluationModelTargetKind.InstalledModel;

    public Guid? SourceArtifactId { get; set; }

    /// <summary>Where the hold-out samples are read from. Real FK to <c>training_datasets.id</c>, restricted delete.</summary>
    public Guid DatasetId { get; set; }

    /// <summary>The dataset's fingerprint when the membership was frozen — a later edit is detectable, not silent.</summary>
    public string DatasetContentFingerprint { get; set; } = string.Empty;

    /// <summary>
    ///     The frozen hold-out membership as UTF-8 JSON. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>training_evaluation_membership_json</c>. Required — an evaluation without a membership scores nothing.
    /// </summary>
    public byte[] MembershipJson { get; set; } = [];

    public TrainingEvaluationStatus Status { get; set; }

    /// <summary>
    ///     Per-sample verdicts as UTF-8 JSON, appended idempotently by sample id. Same treatment under AAD column name
    ///     <c>training_evaluation_results_json</c>. Null until the first sample is scored.
    /// </summary>
    public byte[]? ResultsJson { get; set; }

    /// <summary>How many samples the membership holds. Plaintext so "how far along is it" is a query, not a decrypt.</summary>
    public int TotalCount { get; set; }

    /// <summary>How many of them carry a verdict. Doubles as the resume cursor's bound.</summary>
    public int ScoredCount { get; set; }

    public int PassedCount { get; set; }

    /// <summary>
    ///     Per-kind totals as small UTF-8 JSON, <c>{"kind":{"total":n,"passed":n}}</c>. Deliberately PLAINTEXT: a sample
    ///     kind is a vocabulary label, not operator content, and the comparison report reads it on every list.
    /// </summary>
    public string? PerKindJson { get; set; }

    /// <summary>Encrypted runtime/model provenance captured by the controlled evaluation harness.</summary>
    public byte[]? ExecutionProvenanceJson { get; set; }

    /// <summary>Sanitized failure message. Plaintext and bounded — operator-facing, not model output.</summary>
    public string? ErrorMessage { get; set; }

    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
