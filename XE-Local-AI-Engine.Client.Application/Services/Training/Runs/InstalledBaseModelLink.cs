namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>An installed GGUF that stands in for a base checkpoint: the model an adapter is served against and the base side of a comparison.</summary>
public sealed class InstalledBaseModelLink
{
    public required string ModelName { get; init; }

    public required string RepoId { get; init; }

    public required string? ContentFingerprint { get; init; }
}
