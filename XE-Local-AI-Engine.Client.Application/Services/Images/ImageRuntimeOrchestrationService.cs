namespace XE_Local_AI_Engine.Client.Services.Images;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     The image runtime and source-build Operator endpoints' only door onto the stable-diffusion.cpp provider: the
///     managed-runtime record behind the runtime status, the eject boundary, the prerequisite checklist, the start /
///     cancel / remove / status verbs of the managed source build, and the activity snapshot a <c>409</c> blocked
///     envelope carries. An endpoint is the HTTP edge and may not take a concrete provider's contract itself (the
///     endpoint-dependency rule), so each call arrives here unchanged — this type adds no policy of its own, decides
///     nothing about refusals, outcomes or the OS gate, and re-exposes only the members those eight endpoints call.
///     The build service's host-start recovery and shutdown drain, the activity gate's leases, the installed-runtime
///     writes, and the supervisor's per-model ensure / restart / evict verbs stay off this surface deliberately: no
///     endpoint asks for them.
/// </summary>
public sealed class ImageRuntimeOrchestrationService(
    IStableDiffusionCppSourceBuildService buildService,
    IStableDiffusionCppSourceBuildPrerequisiteProbe prerequisiteProbe,
    IStableDiffusionInstalledRuntimeStore installedRuntimeStore,
    IImageRuntimeActivityGate activityGate,
    IImageServerSupervisor supervisor)
{
    private readonly IImageRuntimeActivityGate _activityGate = activityGate ?? throw new ArgumentNullException(nameof(activityGate));

    private readonly IStableDiffusionCppSourceBuildService _buildService = buildService ?? throw new ArgumentNullException(nameof(buildService));

    private readonly IStableDiffusionInstalledRuntimeStore _installedRuntimeStore =
        installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));

    private readonly IStableDiffusionCppSourceBuildPrerequisiteProbe _prerequisiteProbe =
        prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));

    private readonly IImageServerSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    /// <summary>The managed-runtime record, or <see langword="null" /> when no managed runtime is installed.</summary>
    public Task<StableDiffusionInstalledRuntimeState?> ReadInstalledRuntimeAsync(CancellationToken ct)
    {
        return _installedRuntimeStore.ReadAsync(ct);
    }

    /// <summary>
    ///     A process-wide snapshot of image-runtime activity, which is what a blocked refusal reports so the operator is
    ///     told what to wait for.
    /// </summary>
    public ImageRuntimeActivitySnapshot GetActivitySnapshot()
    {
        return _activityGate.GetSnapshot();
    }

    /// <summary>Ejects every idle resident <c>sd-server</c>. Returns busy without eviction while work is in flight.</summary>
    public Task<ImageServerEvictAllResult> EvictAllAsync(CancellationToken ct)
    {
        return _supervisor.EvictAllAsync(ct);
    }

    /// <summary>The toolchain checklist for one backend, so a refusal can name which prerequisite is missing.</summary>
    public Task<StableDiffusionCppSourceBuildPrerequisiteReport> ProbeAsync(SdGpuBackend backend, CancellationToken ct)
    {
        return _prerequisiteProbe.ProbeAsync(backend, ct);
    }

    /// <summary>Validates the request, checks prerequisites, and detaches the build. Returns as soon as it starts.</summary>
    public Task<StableDiffusionCppSourceBuildStartResult> StartAsync(StableDiffusionCppSourceBuildRequest request, CancellationToken ct)
    {
        return _buildService.StartAsync(request, ct);
    }

    /// <summary>Deletes the adopted managed runtime and its record. The in-app recovery from a fail-closed tombstone.</summary>
    public Task<StableDiffusionCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct)
    {
        return _buildService.RemoveAsync(ct);
    }

    /// <summary>The current snapshot; never blocks on the build.</summary>
    public StableDiffusionCppSourceBuildStatus GetStatus()
    {
        return _buildService.GetStatus();
    }

    /// <summary>Requests cancellation of a running build. False when there is nothing to cancel.</summary>
    public bool Cancel()
    {
        return _buildService.Cancel();
    }
}
