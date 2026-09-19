namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentChangedFile
{
    public required string Path { get; init; }

    public required string ChangeType { get; init; }

    public string? PreviousPath { get; init; }
}

internal sealed class DevelopmentPatchEvidence
{
    public required string BaseCommit { get; init; }

    public required string PatchHash { get; init; }

    public required string ManifestHash { get; init; }

    public required string SubjectHash { get; init; }

    public required string ExpectedResultHash { get; init; }

    public required byte[] PatchBytes { get; init; }

    public required byte[] ManifestBytes { get; init; }

    public required IReadOnlyList<DevelopmentChangedFile> ChangedFiles { get; init; }
}
