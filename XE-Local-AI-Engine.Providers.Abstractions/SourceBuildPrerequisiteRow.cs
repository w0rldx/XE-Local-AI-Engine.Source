namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>One prerequisite checklist row of a managed runtime.</summary>
public sealed record SourceBuildPrerequisiteRow
{
    /// <summary>Stable identifier the SPA keys on (<c>os-is-linux</c>, <c>cmake</c>, <c>free-disk</c>, …).</summary>
    public required string Key { get; init; }

    public required bool Satisfied { get; init; }

    /// <summary>A short, display-safe explanation free of paths and secrets — usually the tool's own first version line.</summary>
    public required string Detail { get; init; }
}
