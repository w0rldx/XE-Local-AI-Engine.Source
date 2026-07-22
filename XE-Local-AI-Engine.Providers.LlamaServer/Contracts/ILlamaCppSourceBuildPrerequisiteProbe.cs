namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed record LlamaCppSourceBuildPrerequisiteItem(string Key, bool Satisfied, string Detail);

public sealed record LlamaCppSourceBuildPrerequisiteReport(bool CanBuild, IReadOnlyList<LlamaCppSourceBuildPrerequisiteItem> Items);

public interface ILlamaCppSourceBuildPrerequisiteProbe
{
    Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct);
}
