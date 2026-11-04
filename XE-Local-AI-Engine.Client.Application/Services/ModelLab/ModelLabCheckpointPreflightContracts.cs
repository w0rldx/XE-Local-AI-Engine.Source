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
    UnsupportedModelType
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

    public int? CheckpointHiddenSize { get; init; }

    public int? CheckpointBlockCount { get; init; }

    public long? GgufEmbeddingLength { get; init; }

    public long? GgufBlockCount { get; init; }

    /// <summary>Blocks llama.cpp can apply a vector at: an MTP block counts in <see cref="GgufBlockCount" />, but a vector there is dropped.</summary>
    public long? GgufSteerableBlockCount { get; init; }

    /// <summary>Each block's kind from the checkpoint's config, or null when it does not say.</summary>
    public IReadOnlyList<string>? CheckpointLayerTypes { get; init; }

    /// <summary>The layer a steering job takes when none is named, or null on a model too short to steer.</summary>
    public int? DefaultSteeringLayer { get; init; }

    /// <summary>The text the operator confirms before the download, set once the checkpoint's license has been read.</summary>
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
