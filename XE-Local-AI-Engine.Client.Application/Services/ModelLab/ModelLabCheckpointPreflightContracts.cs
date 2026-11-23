namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Why a checkpoint and GGUF pick cannot run in Model Lab. The value is the API's stable refusal code.</summary>
public enum ModelLabCheckpointRefusal
{
    UnsupportedPlatform,
    NotTrainable,
    VramUnknown,
    WeightSizeUnknown,
    CannotFitCard,
    ConfigUnreadable,
    GgufNotInstalled,
    GgufUnreadable,
    EmbeddingMismatch,
    BlockCountMismatch,
    LicenseNotConfirmed,
    DownloadRejected,
    UnsupportedModelType,
    ArchitectureMismatch
}

/// <summary>Why a trace of a checkpoint cannot record attention. The value is the API's stable code.</summary>
public enum ModelLabAttentionRefusal
{
    UnsupportedModelType,
    NotFullAttention,
    ShapeNotStated,
    NoFullAttentionBlock,
    HeadCountOutOfRange
}

/// <summary>Why a checkpoint cannot be traced while it can still be steered. The value is the API's stable code.</summary>
public enum ModelLabTraceRefusal
{
    RouterLogitsRequested
}

/// <summary>The sizes and shapes behind a preflight answer. A value stays null until the check that reads it has run.</summary>
public sealed record ModelLabCheckpointPreflight
{
    public required string RepoId { get; init; }

    /// <summary>The commit the request resolved to.</summary>
    public string? Revision { get; init; }

    public long? WeightBytes { get; init; }

    public long? RequiredVramBytes { get; init; }

    public long? CardVramBytes { get; init; }

    public string? License { get; init; }

    public bool IsGated { get; init; }

    /// <summary>False when no licensing metadata was found, which is not the same as a repository that declares no license.</summary>
    public bool MetadataPresent { get; init; }

    public int? CheckpointHiddenSize { get; init; }

    public int? CheckpointBlockCount { get; init; }

    public long? GgufEmbeddingLength { get; init; }

    public long? GgufBlockCount { get; init; }

    /// <summary>Blocks llama.cpp can apply a vector at: an MTP block counts in <see cref="GgufBlockCount" />, but a vector there is dropped.</summary>
    public long? GgufSteerableBlockCount { get; init; }

    /// <summary>The config's <c>model_type</c>, or null when it does not say.</summary>
    public string? ModelType { get; init; }

    /// <summary>
    ///     Each block's kind by the traced family's rule: an OLMoE block is always full attention; a Qwen3.5 config's own list, else its full-attention
    ///     interval. Null for another family, or when neither says.
    /// </summary>
    public IReadOnlyList<string>? CheckpointLayerTypes { get; init; }

    /// <summary>The config's query heads per block, or null when it does not say.</summary>
    public int? CheckpointAttentionHeads { get; init; }

    /// <summary>Why a trace of this checkpoint cannot record attention, or null when it can. Set once the config has been read.</summary>
    public string? AttentionRefusalMessage { get; init; }

    /// <summary>The code of <see cref="AttentionRefusalMessage" />, set together with it.</summary>
    public ModelLabAttentionRefusal? AttentionRefusal { get; init; }

    /// <summary>The config was read and nothing in it stops attention being recorded.</summary>
    public bool AttentionSupported => ModelType is not null && AttentionRefusalMessage is null;

    /// <summary>Why this checkpoint cannot be traced while it can still be steered, or null when it can. Set once the config has been read.</summary>
    public string? TraceRefusalMessage { get; init; }

    /// <summary>The code of <see cref="TraceRefusalMessage" />, set together with it.</summary>
    public ModelLabTraceRefusal? TraceRefusal { get; init; }

    /// <summary>The layer a steering job takes when none is named, or null on a model too short to steer.</summary>
    public int? DefaultSteeringLayer { get; init; }

    /// <summary>The canonical English text of what the operator confirms, set once the checkpoint's license has been read. It is what the row records and hashes; the page shows a translation built from the same facts.</summary>
    public string? LicenseConfirmationText { get; init; }

    public ModelLabCheckpointRefusal? Refusal { get; init; }

    /// <summary>The operator-facing reason, set together with <see cref="Refusal" />.</summary>
    public string? RefusalMessage { get; init; }
}

/// <summary>A preflight answer for a download request, and the artifact when it was allowed to start.</summary>
public sealed record ModelLabCheckpointDownload
{
    public required ModelLabCheckpointPreflight Preflight { get; init; }

    public Guid? BaseArtifactId { get; init; }

    public string? ArtifactStatus { get; init; }
}
