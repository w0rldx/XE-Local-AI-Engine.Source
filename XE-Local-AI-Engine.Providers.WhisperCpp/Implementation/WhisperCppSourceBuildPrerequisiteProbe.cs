namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Bounded Linux toolchain checks, run before a managed whisper.cpp build is allowed to start.
/// </summary>
/// <remarks>
///     The post-build relocation gate runs <c>readelf</c>, so binutils is on this runtime's checklist floor.
/// </remarks>
public sealed class WhisperCppSourceBuildPrerequisiteProbe : SourceBuildPrerequisiteProbe, IWhisperCppSourceBuildPrerequisiteProbe
{
    public WhisperCppSourceBuildPrerequisiteProbe()
        : this(RuntimeCacheDirectory.Resolve(), SourceBuildPolicy.RequiredFreeDiskBytes)
    {
    }

    internal WhisperCppSourceBuildPrerequisiteProbe(string cacheRoot, long requiredFreeDiskBytes = SourceBuildPolicy.RequiredFreeDiskBytes)
        : base(cacheRoot, requiredFreeDiskBytes, "whisper.cpp", requiresReadelf: true)
    {
    }

    /// <inheritdoc />
    public Task<SourceBuildPrerequisiteReport> ProbeAsync(WhisperBackend backend, CancellationToken ct)
    {
        return ProbeReportAsync(token => ProbeBackendToolsAsync(backend, token), ct);
    }

    // CUDA is the only accelerated backend whisper has here, so there is no Vulkan branch to mirror the image
    // runtime's. A CPU build needs nothing beyond the shared floor.
    private Task<IReadOnlyList<SourceBuildPrerequisiteRow>> ProbeBackendToolsAsync(WhisperBackend backend, CancellationToken ct)
    {
        return backend == WhisperBackend.Cuda ? ProbeCudaToolsAsync(ct) : Task.FromResult<IReadOnlyList<SourceBuildPrerequisiteRow>>([]);
    }
}
