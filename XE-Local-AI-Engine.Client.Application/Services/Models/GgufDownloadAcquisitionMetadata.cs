namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed record GgufDownloadAcquisitionMetadata
{
    public required string RepoId { get; init; }

    public required string ResolvedRevision { get; init; }

    public required string SourceDisplayName { get; init; }

    public required long DeclaredSizeBytes { get; init; }

    public required string? DeclaredSha256 { get; init; }

    public required GgufRole Role { get; init; }
}
