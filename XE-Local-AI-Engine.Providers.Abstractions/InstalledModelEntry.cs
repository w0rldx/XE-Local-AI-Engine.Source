namespace XE_Local_AI_Engine.Providers.Abstractions;

public sealed class InstalledModelEntry
{
    /// <summary>Raw model name/tag as reported by the runtime (caller normalizes).</summary>
    public required string? Name { get; init; }

    /// <summary>Raw content digest as reported by the runtime, when available.</summary>
    public required string? Digest { get; init; }
}
