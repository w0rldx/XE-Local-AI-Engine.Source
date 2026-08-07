namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record DevelopmentEvidenceSet(
    DevelopmentPatchEvidence Current,
    DevelopmentArtifactSnapshot PatchArtifact,
    DevelopmentArtifactSnapshot ManifestArtifact,
    ReadOnlyMemory<byte> Patch,
    ReadOnlyMemory<byte> Manifest);

internal sealed record DevelopmentPreparedArtifact(Guid ArtifactId, DevelopmentAttachArtifactCommand Attachment);
