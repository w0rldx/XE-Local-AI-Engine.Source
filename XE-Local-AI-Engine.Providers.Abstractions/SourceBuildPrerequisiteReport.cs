namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The whole prerequisite checklist of a managed runtime, plus the one flag that decides whether its build or install may start.</summary>
public sealed record SourceBuildPrerequisiteReport
{
    /// <summary>True only when every row is satisfied.</summary>
    public required bool CanBuild { get; init; }

    public required IReadOnlyList<SourceBuildPrerequisiteRow> Items { get; init; }
}
