namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>Bounded Linux source-build prerequisite checks.</summary>
public sealed class StableDiffusionCppSourceBuildPrerequisiteProbe : SourceBuildPrerequisiteProbe, IStableDiffusionCppSourceBuildPrerequisiteProbe
{
    public StableDiffusionCppSourceBuildPrerequisiteProbe()
        : this(RuntimeCacheDirectory.Resolve(), SourceBuildPolicy.RequiredFreeDiskBytes)
    {
    }

    internal StableDiffusionCppSourceBuildPrerequisiteProbe(string cacheRoot, long requiredFreeDiskBytes = SourceBuildPolicy.RequiredFreeDiskBytes)
        : base(cacheRoot, requiredFreeDiskBytes, "stable-diffusion.cpp", requiresReadelf: false)
    {
    }

    public async Task<StableDiffusionCppSourceBuildPrerequisiteReport> ProbeAsync(SdGpuBackend backend, CancellationToken ct)
    {
        var rows = await ProbeRowsAsync(token => ProbeBackendToolsAsync(backend, token), ct).ConfigureAwait(false);
        return new StableDiffusionCppSourceBuildPrerequisiteReport
        {
            CanBuild = rows.All(static row => row.Satisfied),
            Items =
            [
                .. rows.Select(static row => new StableDiffusionCppSourceBuildPrerequisiteItem
                {
                    Key = row.Key,
                    Satisfied = row.Satisfied,
                    Detail = row.Detail
                })
            ]
        };
    }

    private async Task<IReadOnlyList<SourceBuildPrerequisiteRow>> ProbeBackendToolsAsync(SdGpuBackend backend, CancellationToken ct)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => await ProbeCudaToolsAsync(ct).ConfigureAwait(false),
            SdGpuBackend.Vulkan =>
            [
                await ProbeToolAsync("glslc", ["--version"], "Vulkan shader compiler (glslc)", ct).ConfigureAwait(false),
                await ProbeToolAsync("vulkaninfo", ["--summary"], "Vulkan runtime probe", ct).ConfigureAwait(false)
            ],
            _ => []
        };
    }
}
