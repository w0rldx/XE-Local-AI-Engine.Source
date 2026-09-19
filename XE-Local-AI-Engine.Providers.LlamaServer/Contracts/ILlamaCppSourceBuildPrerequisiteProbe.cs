namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class LlamaCppSourceBuildPrerequisiteItem
{
    public required string Key { get; init; }

    public required bool Satisfied { get; init; }

    public required string Detail { get; init; }
}

public sealed record LlamaCppSourceBuildPrerequisiteReport
{
    public required bool CanBuild { get; init; }

    public required IReadOnlyList<LlamaCppSourceBuildPrerequisiteItem> Items { get; init; }
}

public interface ILlamaCppSourceBuildPrerequisiteProbe
{
    Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct);
}
