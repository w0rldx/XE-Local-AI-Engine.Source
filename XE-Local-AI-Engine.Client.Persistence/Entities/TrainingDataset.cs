namespace XE_Local_AI_Engine.Client.Persistence.Entities;

internal sealed record class TrainingDataset
{
    public Guid Id { get; set; }

    /// <summary>Owning definition. Real FK to <c>training_dataset_definitions.id</c>, restricted delete; indexed.</summary>
    public Guid DefinitionId { get; set; }

    /// <summary>The <see cref="TrainingDatasetDefinition.DefinitionVersion" /> this dataset was generated from.</summary>
    public long DefinitionVersion { get; set; }

    /// <summary>
    ///     The definition BODY as it stood when this dataset was created, copied in the same transaction that read
    ///     <see cref="DefinitionVersion" />. Generation and evaluation read this snapshot, never the live definition row:
    ///     an edit between creation and generation would otherwise change teacher/tools/instructions while the dataset
    ///     still claimed the old version. Null only for a dataset created before pinning existed, which both executors
    ///     refuse with a visible reason rather than silently falling back to the live body.
    /// </summary>
    public byte[]? DefinitionJson { get; set; }

    public string Name { get; set; } = string.Empty;

    public TrainingDatasetStatus Status { get; set; }

    /// <summary>Bumped by <em>any</em> sample mutation; a run freezes against the revision it read.</summary>
    public int Revision { get; set; }

    /// <summary>
    ///     <c>v1:&lt;64hex&gt;</c> over the ordered sample content and labels, recomputed on every revision bump. Null
    ///     while the dataset is still generating. Plaintext (structural — it is the freeze key).
    /// </summary>
    public string? ContentFingerprint { get; set; }

    public int TotalSampleCount { get; set; }

    public int GoodSampleCount { get; set; }

    public int BadSampleCount { get; set; }

    /// <summary>Samples the generation pipeline rejected with a recorded reason — never silently dropped.</summary>
    public int RejectedSampleCount { get; set; }

    /// <summary>Samples skipped because their <see cref="TrainingDatasetSample.SourceHash" /> already existed here.</summary>
    public int DuplicateSampleCount { get; set; }

    public long Version { get; set; }

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
