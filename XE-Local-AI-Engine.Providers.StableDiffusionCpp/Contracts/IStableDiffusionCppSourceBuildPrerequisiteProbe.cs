namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed record StableDiffusionCppSourceBuildPrerequisiteItem(string Key, bool Satisfied, string Detail);

public sealed record StableDiffusionCppSourceBuildPrerequisiteReport(
    bool CanBuild,
    IReadOnlyList<StableDiffusionCppSourceBuildPrerequisiteItem> Items);

public interface IStableDiffusionCppSourceBuildPrerequisiteProbe
{
    Task<StableDiffusionCppSourceBuildPrerequisiteReport> ProbeAsync(SdGpuBackend backend, CancellationToken ct);
}
