namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>A file this operation created and may therefore delete when compensating. Artifacts it did not create are never touched.</summary>
internal sealed class OwnedArtifact
{
    public required string Path { get; init; }

    public required bool Owned { get; init; }
}
