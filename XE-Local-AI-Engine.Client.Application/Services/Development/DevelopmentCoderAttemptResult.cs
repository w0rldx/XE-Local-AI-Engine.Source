namespace XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentCoderAttemptResult
{
    public required Guid AttemptId { get; init; }

    public required string BaseCommit { get; init; }

    public required string SubjectHash { get; init; }

    public required string PatchHash { get; init; }

    public required string ManifestHash { get; init; }

    public required IReadOnlyList<string> ChangedFiles { get; init; }
}
