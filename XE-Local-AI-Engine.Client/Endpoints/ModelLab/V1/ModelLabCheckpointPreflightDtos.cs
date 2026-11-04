namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class ModelLabCheckpointPreflightRequest
{
    /// <summary>A checkpoint already on this node, checked without contacting Hugging Face. Replaces <see cref="RepoId" /> and <see cref="Revision" />.</summary>
    public Guid? BaseArtifactId { get; init; }

    /// <summary>The Hugging Face base checkpoint repository to trace.</summary>
    public string? RepoId { get; init; }

    /// <summary>Optional commit or branch. The response states the commit it resolved to.</summary>
    public string? Revision { get; init; }

    /// <summary>The installed GGUF that will apply the vector.</summary>
    public required string InstalledModelName { get; init; }
}

/// <summary>A refused pick is a 200 with <see cref="Refusal" /> set, so the page shows every reason through one path.</summary>
public sealed class ModelLabCheckpointPreflightResponse
{
    public required string RepoId { get; init; }

    public required string? Revision { get; init; }

    public required long? WeightBytes { get; init; }

    public required long? RequiredVramBytes { get; init; }

    public required long? CardVramBytes { get; init; }

    public required string? License { get; init; }

    public required bool IsGated { get; init; }

    public required int? CheckpointHiddenSize { get; init; }

    public required int? CheckpointBlockCount { get; init; }

    public required long? GgufEmbeddingLength { get; init; }

    public required long? GgufBlockCount { get; init; }

    /// <summary>Blocks a steering vector can reach. A layer must be 1 or more and below this.</summary>
    public required long? SteerableBlockCount { get; init; }

    /// <summary>Each block's kind from the checkpoint's config, in order, or null when it does not say.</summary>
    public required IReadOnlyList<string>? CheckpointLayerTypes { get; init; }

    /// <summary>The layer a steering job takes when none is named.</summary>
    public required int? DefaultSteeringLayer { get; init; }

    /// <summary>The text the operator confirms before the download. Null until the checkpoint's license has been read.</summary>
    public required string? LicenseConfirmationText { get; init; }

    public required ModelLabCheckpointRefusal? Refusal { get; init; }

    public required string? RefusalMessage { get; init; }
}
