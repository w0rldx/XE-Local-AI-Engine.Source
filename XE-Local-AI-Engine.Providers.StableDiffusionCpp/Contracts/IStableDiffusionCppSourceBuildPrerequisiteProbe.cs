namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;

public interface IStableDiffusionCppSourceBuildPrerequisiteProbe
{
    Task<SourceBuildPrerequisiteReport> ProbeAsync(SdGpuBackend backend, CancellationToken ct);
}
