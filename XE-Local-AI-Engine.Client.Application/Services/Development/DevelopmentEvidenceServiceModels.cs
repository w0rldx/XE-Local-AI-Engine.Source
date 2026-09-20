namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class DevelopmentEvidenceSet
{
    public required DevelopmentPatchEvidence Current { get; init; }

    public required DevelopmentArtifactSnapshot PatchArtifact { get; init; }

    public required DevelopmentArtifactSnapshot ManifestArtifact { get; init; }

    public required ReadOnlyMemory<byte> Patch { get; init; }

    public required ReadOnlyMemory<byte> Manifest { get; init; }
}

internal sealed class DevelopmentPreparedArtifact
{
    public required Guid ArtifactId { get; init; }

    public required DevelopmentAttachArtifactCommand Attachment { get; init; }
}

/// <summary>
///     An artifact row paired with what was read out of it — raw bytes, or a report deserialized from them.
/// </summary>
/// <remarks>
///     The row travels with the payload because every downstream authorization check compares both the report's own
///     claims and the row's stamped protocol version, profile digest and attempt id; reading them apart is how the
///     two drift.
/// </remarks>
internal sealed record DevelopmentArtifactWith<TPayload>(DevelopmentArtifactSnapshot Artifact, TPayload Payload);
