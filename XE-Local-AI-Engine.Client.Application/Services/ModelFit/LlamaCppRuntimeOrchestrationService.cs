namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The llama.cpp runtime, source-build and running-model Operator endpoints' only door onto the
///     <c>Providers.LlamaServer</c> contracts: the prerequisite checklists, the start / cancel / status / remove verbs of
///     the managed source build and its CUDA-only legacy twin, the running-process count and health snapshot, the eject
///     boundary, and the installed-runtime record plus update snapshot a runtime-status response is built from. An
///     endpoint is the HTTP edge and may not take a concrete provider's contract itself (the endpoint-dependency rule),
///     so each call arrives here unchanged — this type adds no policy of its own, decides nothing about refusals,
///     outcomes, the OS gate or the keep-model-warm gate, and re-exposes only the members those twelve endpoints call.
///     The one exception is the shared remove gate below, which is behaviour the endpoints already delegated to a static
///     helper and which moved here with it rather than being duplicated per endpoint.
///     <para>
///         Deliberately off this surface, because no endpoint asks for them: the binary manager's ensure / install /
///         adopt verbs, the build services' <c>RecoverAsync</c> / <c>ShutdownAsync</c> / <c>CancelLegacyPinnedCuda</c> /
///         <c>RecoverStaleWorkDirectoryAsync</c>, the installed-runtime store's lock / write / delete, the update state's
///         <c>Store</c>, the activity reservation's <c>TryReserve</c> / <c>TryRelease</c>, and the supervisor's
///         ensure-running, evict, lease, profiling and benchmark surface.
///     </para>
/// </summary>
public sealed class LlamaCppRuntimeOrchestrationService(
    ICudaBuildPrerequisiteProbe cudaPrerequisiteProbe,
    ICudaBuildService cudaBuildService,
    IInstalledRuntimeStore installedRuntimeStore,
    ILlamaCppBinaryManager binaryManager,
    ILlamaCppSourceBuildActivity buildActivity,
    ILlamaCppSourceBuildPrerequisiteProbe sourceBuildPrerequisiteProbe,
    ILlamaCppSourceBuildService sourceBuildService,
    ILlamaCppUpdateState updateState,
    ILlamaServerProcessSupervisor supervisor)
{
    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));

    private readonly ILlamaCppSourceBuildActivity _buildActivity = buildActivity ?? throw new ArgumentNullException(nameof(buildActivity));

    private readonly ICudaBuildService _cudaBuildService = cudaBuildService ?? throw new ArgumentNullException(nameof(cudaBuildService));

    private readonly ICudaBuildPrerequisiteProbe _cudaPrerequisiteProbe =
        cudaPrerequisiteProbe ?? throw new ArgumentNullException(nameof(cudaPrerequisiteProbe));

    private readonly IInstalledRuntimeStore _installedRuntimeStore =
        installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));

    private readonly ILlamaCppSourceBuildPrerequisiteProbe _sourceBuildPrerequisiteProbe =
        sourceBuildPrerequisiteProbe ?? throw new ArgumentNullException(nameof(sourceBuildPrerequisiteProbe));

    private readonly ILlamaCppSourceBuildService _sourceBuildService =
        sourceBuildService ?? throw new ArgumentNullException(nameof(sourceBuildService));

    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    private readonly ILlamaCppUpdateState _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));

    /// <summary>The last observed llama.cpp update/runtime snapshot, which a runtime-status response is rendered from.</summary>
    public LlamaCppUpdateSnapshot CurrentUpdateSnapshot => _updateState.Current;

    /// <summary>The CUDA build toolchain checklist, so the UI can enable the build only when every item is satisfied.</summary>
    public Task<CudaBuildPrerequisiteReport> ProbeCudaBuildPrerequisitesAsync(CancellationToken ct)
    {
        return _cudaPrerequisiteProbe.ProbeAsync(ct);
    }

    /// <summary>The source-build toolchain checklist for one backend, so a refusal can name which prerequisite is missing.</summary>
    public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeSourceBuildPrerequisitesAsync(LlamaCppSourceBackend backend, CancellationToken ct)
    {
        return _sourceBuildPrerequisiteProbe.ProbeAsync(backend, ct);
    }

    /// <summary>The legacy CUDA build's current status; never blocks on the build.</summary>
    public CudaBuildStatus GetCudaBuildStatus()
    {
        return _cudaBuildService.GetStatus();
    }

    /// <summary>Requests cancellation of a running CUDA build. False when there is nothing to cancel.</summary>
    public bool CancelCudaBuild()
    {
        return _cudaBuildService.Cancel();
    }

    /// <summary>Validates the request, checks prerequisites, and detaches the build. Returns as soon as it starts.</summary>
    public Task<LlamaCppSourceBuildStartResult> StartSourceBuildAsync(LlamaCppSourceBuildRequest request, CancellationToken ct)
    {
        return _sourceBuildService.StartAsync(request, ct);
    }

    /// <summary>The managed source build's current status; never blocks on the build.</summary>
    public LlamaCppSourceBuildStatus GetSourceBuildStatus()
    {
        return _sourceBuildService.GetStatus();
    }

    /// <summary>Requests cancellation of a running source build. False when there is nothing to cancel.</summary>
    public bool CancelSourceBuild()
    {
        return _sourceBuildService.Cancel();
    }

    /// <summary>How many <c>llama-server</c> processes are alive — what an eject-first refusal reports back.</summary>
    public int CountRunningProcesses()
    {
        return _supervisor.CountRunningProcesses();
    }

    /// <summary>The managed-runtime record, or <see langword="null" /> when no managed runtime is installed.</summary>
    public Task<InstalledRuntimeState?> ReadInstalledRuntimeAsync(CancellationToken ct)
    {
        return _installedRuntimeStore.ReadAsync(ct);
    }

    /// <summary>
    ///     Ejects one running <c>(model, role)</c> process. Graceful by default — in-flight work is given a bounded drain
    ///     window and the process is left running when it does not drain; <paramref name="force" /> tears it down anyway.
    /// </summary>
    public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        return _supervisor.EjectAsync(modelName, role, force, ct);
    }

    /// <summary>One row per running <c>(model, role)</c> process — the only seam the running-models list is derived from.</summary>
    public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct)
    {
        return _supervisor.CheckHealthAsync(ct);
    }

    /// <summary>Runs the shared remove gate over the generalized source build's removal.</summary>
    public Task<LlamaCppRuntimeRemovalOutcome> TryRemoveSourceBuildAsync(CancellationToken ct)
    {
        return TryRemoveAsync(_supervisor, _buildActivity, _binaryManager.RemoveSourceBuildAsync, ct);
    }

    /// <summary>Runs the shared remove gate over the legacy CUDA source build's removal.</summary>
    public Task<LlamaCppRuntimeRemovalOutcome> TryRemoveCudaBuildAsync(CancellationToken ct)
    {
        return TryRemoveAsync(_supervisor, _buildActivity, _binaryManager.RemoveCudaSourceBuildAsync, ct);
    }

    internal static bool IsSourceBuildActive(ILlamaCppSourceBuildActivity sourceBuildActivity)
    {
        return sourceBuildActivity.ActiveBuildId is not null;
    }

    /// <summary>
    ///     Shared remove gate for the managed source-build runtimes (generic + CUDA). Refuses while a source build is
    ///     active — re-checked AFTER the mutation lease is taken so a build that starts during acquisition still blocks —
    ///     refuses when the lease cannot be taken or any llama-server process is still running (eject-first), and only
    ///     then runs <paramref name="removeAsync" /> while holding the lease. The lease is disposed on every path.
    /// </summary>
    internal static async Task<LlamaCppRuntimeRemovalOutcome> TryRemoveAsync(ILlamaServerProcessSupervisor processSupervisor,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
        Func<CancellationToken, Task> removeAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(removeAsync);

        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return new LlamaCppRuntimeRemovalOutcome(Removed: false, RunningProcessCount: 0, BuildActive: true);
        }

        await using var mutationLease = await processSupervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false);
        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return new LlamaCppRuntimeRemovalOutcome(Removed: false, RunningProcessCount: 0, BuildActive: true);
        }

        var runningProcessCount = processSupervisor.CountRunningProcesses();
        if (mutationLease is null || runningProcessCount > 0)
        {
            return new LlamaCppRuntimeRemovalOutcome(Removed: false, runningProcessCount, BuildActive: false);
        }

        await removeAsync(ct).ConfigureAwait(false);
        return new LlamaCppRuntimeRemovalOutcome(Removed: true, runningProcessCount, BuildActive: false);
    }
}

/// <summary>
///     The result of the shared remove gate: whether the removal ran, how many llama-server processes were still
///     running when the gate was evaluated, and whether a source build blocked it.
/// </summary>
public sealed record LlamaCppRuntimeRemovalOutcome(bool Removed, int RunningProcessCount, bool BuildActive);
