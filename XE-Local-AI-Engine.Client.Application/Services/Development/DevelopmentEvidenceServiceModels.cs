namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record DevelopmentEvidenceSet(
    DevelopmentPatchEvidence Current,
    DevelopmentArtifactSnapshot PatchArtifact,
    DevelopmentArtifactSnapshot ManifestArtifact,
    ReadOnlyMemory<byte> Patch,
    ReadOnlyMemory<byte> Manifest);

internal sealed record DevelopmentPreparedArtifact(Guid ArtifactId, DevelopmentAttachArtifactCommand Attachment);

/// <summary>
///     An artifact row paired with what was read out of it — raw bytes, or a report deserialized from them. The row
///     travels with the payload because every authorization check downstream compares BOTH (the report's own claims and
///     the artifact row's stamped protocol version, profile digest and attempt id), and reading them apart is how the
///     two drift.
/// </summary>
internal sealed record DevelopmentArtifactWith<TPayload>(DevelopmentArtifactSnapshot Artifact, TPayload Payload);
