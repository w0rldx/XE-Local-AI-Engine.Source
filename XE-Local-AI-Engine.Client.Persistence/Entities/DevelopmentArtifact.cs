namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class DevelopmentArtifact
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public Guid TaskId { get; set; }

    public Guid? AttemptId { get; set; }

    public DevelopmentArtifactKind Kind { get; set; }

    public int SchemaVersion { get; set; }

    public byte[]? PayloadJson { get; set; }

    public string? StorageKey { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public long ByteCount { get; set; }

    public long CreatedAtUtc { get; set; }

    public string BaseCommitHash { get; set; } = string.Empty;

    public string SubjectHash { get; set; } = string.Empty;

    public string ChangedFilesManifestHash { get; set; } = string.Empty;

    public byte[]? InputArtifactIdsJson { get; set; }

    public string? CommandProfileVersion { get; set; }

    public bool IsValid { get; set; } = true;
}
