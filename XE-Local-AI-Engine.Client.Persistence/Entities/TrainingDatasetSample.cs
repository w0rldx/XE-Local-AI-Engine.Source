namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class TrainingDatasetSample
{
    public Guid Id { get; set; }

    /// <summary>Owning dataset. Real FK to <c>training_datasets.id</c>; also the AAD binding for both encrypted columns.</summary>
    public Guid DatasetId { get; set; }

    /// <summary>Stable ordering within the dataset — the fingerprint and the frozen membership both read it.</summary>
    public int Sequence { get; set; }

    /// <summary>
    ///     Sample kind within the dataset's <see cref="TrainingDatasetKind" /> (the split is stratified by it and the
    ///     evaluation aggregates group by it). Plaintext free-form string, not an enum: the tool-calling vocabulary is
    ///     owned by the generation service, and a closed enum here would force a migration every time it grows.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public TrainingSampleLabel Label { get; set; }

    public TrainingSampleReviewState ReviewState { get; set; }

    /// <summary>
    ///     The trajectory as UTF-8 JSON in the chat <c>parts[]</c> shape. Plaintext while tracked in memory; encrypted at
    ///     rest by <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name
    ///     <c>training_sample_content_json</c> bound to <see cref="DatasetId" />. Required.
    /// </summary>
    public byte[] ContentJson { get; set; } = [];

    /// <summary>
    ///     Per-layer validation outcomes as UTF-8 JSON. Same AAD binding as <see cref="ContentJson" /> under column name
    ///     <c>training_sample_validation_json</c>. Optional — a sample rejected before validation ran has none.
    /// </summary>
    public byte[]? ValidationJson { get; set; }

    public TrainingSampleProvenance Provenance { get; set; }

    /// <summary>
    ///     SHA-256 hex over the normalized content parts. Plaintext even though the content is encrypted — it is the
    ///     dedup key and has to be queryable (same posture as <see cref="GoldenConversation.SourceMessageId" />).
    /// </summary>
    public string SourceHash { get; set; } = string.Empty;

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
