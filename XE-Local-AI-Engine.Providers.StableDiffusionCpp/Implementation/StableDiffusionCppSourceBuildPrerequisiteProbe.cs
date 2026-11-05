namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
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

    public Task<SourceBuildPrerequisiteReport> ProbeAsync(SdGpuBackend backend, CancellationToken ct)
    {
        return ProbeReportAsync(token => ProbeBackendToolsAsync(backend, token), ct);
    }

    private async Task<IReadOnlyList<SourceBuildPrerequisiteRow>> ProbeBackendToolsAsync(SdGpuBackend backend, CancellationToken ct)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => await ProbeCudaToolsAsync(ct).ConfigureAwait(false),
            SdGpuBackend.Vulkan => await ProbeVulkanToolsAsync(ct).ConfigureAwait(false),
            _ => []
        };
    }
}
