namespace XE_Local_AI_Engine.Client.Services.Models;

public sealed class GgufProjectorAcquisitionMetadata
{
    public required string SourceDisplayName { get; init; }

    public required string DeclaredSha256 { get; init; }

    public required long DeclaredSizeBytes { get; init; }
}
