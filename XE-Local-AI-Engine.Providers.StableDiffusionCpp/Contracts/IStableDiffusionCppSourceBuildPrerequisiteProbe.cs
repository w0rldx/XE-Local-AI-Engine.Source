namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed record StableDiffusionCppSourceBuildPrerequisiteItem
{
    public required string Key { get; init; }

    public required bool Satisfied { get; init; }

    public required string Detail { get; init; }
}

public sealed class StableDiffusionCppSourceBuildPrerequisiteReport
{
    public required bool CanBuild { get; init; }

    public required IReadOnlyList<StableDiffusionCppSourceBuildPrerequisiteItem> Items { get; init; }
}

public interface IStableDiffusionCppSourceBuildPrerequisiteProbe
{
    Task<StableDiffusionCppSourceBuildPrerequisiteReport> ProbeAsync(SdGpuBackend backend, CancellationToken ct);
}
