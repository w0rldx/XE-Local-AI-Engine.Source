namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed record DevelopmentChangedFile(string Path, string ChangeType, string? PreviousPath = null);

internal sealed record DevelopmentPatchEvidence(
    string BaseCommit,
    string PatchHash,
    string ManifestHash,
    string SubjectHash,
    string ExpectedResultHash,
    byte[] PatchBytes,
    byte[] ManifestBytes,
    IReadOnlyList<DevelopmentChangedFile> ChangedFiles);
