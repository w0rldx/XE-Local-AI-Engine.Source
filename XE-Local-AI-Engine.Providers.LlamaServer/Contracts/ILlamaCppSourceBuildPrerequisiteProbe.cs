namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;

public interface ILlamaCppSourceBuildPrerequisiteProbe
{
    Task<SourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct);
}
