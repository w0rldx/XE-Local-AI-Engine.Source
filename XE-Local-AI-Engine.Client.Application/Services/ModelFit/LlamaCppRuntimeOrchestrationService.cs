namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The host's only door onto the <c>Providers.LlamaServer</c> contracts: the prerequisite checklist, the start /
///     cancel / status / remove verbs of the managed source build, the running-process
///     count and health snapshot, the eject boundary, the installed-runtime record plus update snapshot a
///     runtime-status response is built from, and the provision-then-lease pair the inbound model proxy forwards
///     through. A host type may not take a concrete provider's contract itself (the host-dependency rule), so each call
///     arrives here unchanged — this type adds no policy of its own, decides nothing about refusals, outcomes, the OS
///     gate or the keep-model-warm gate, and re-exposes only the members its callers call. The one exception is the
///     shared remove gate below, which is behaviour the endpoints already delegated to a static helper and which moved
///     here with it rather than being duplicated per endpoint.
///     <para>
///         The supervisor's ensure-running and inference-lease surface IS on this type, because
///         <c>Services/Proxy/LocalModelProxyForwarder</c> needs both and stays in the host (it owns the
///         <c>HttpContext</c> an application service may not). The forwarder is the only caller of that pair; the
///         endpoints do not use it.
///     </para>
///     <para>
///         The first-run provisioning trio — GPU-variant probe, binary ensure, acquisition-status report — is here for
///         the same reason: <c>BackgroundServices/FirstRunModelProvisioningService</c> needs all three and stays in the
///         host, because the desktop-launch decision it gates on is a host fact (process args plus the Velopack install
///         kind) that the application layer cannot resolve. It is the only caller of those three.
///     </para>
///     <para>
///         Deliberately off this surface, because nothing in the host asks for them: the binary manager's install and
///         adopt verbs, the build service's <c>RecoverAsync</c> / <c>ShutdownAsync</c>, the binary manager's
///         legacy-only source-build removal, the installed-runtime store's lock / write / delete, the update
///         state's <c>Store</c>, the activity reservation's <c>TryReserve</c> / <c>TryRelease</c>, and the supervisor's
///         evict, profiling and benchmark surface.
///     </para>
/// </summary>
public sealed class LlamaCppRuntimeOrchestrationService
{
    private readonly IRuntimeAcquisitionStatusRegistry _acquisitionStatus;
    private readonly ILlamaCppBinaryManager _binaryManager;

    private readonly ILlamaCppSourceBuildActivity _buildActivity;

    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly ILlamaCppSourceBuildPrerequisiteProbe _sourceBuildPrerequisiteProbe;
    private readonly ILlamaCppSourceBuildService _sourceBuildService;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    private readonly ILlamaCppUpdateState _updateState;

    private readonly IGpuVariantSelector _variantSelector;

    public LlamaCppRuntimeOrchestrationService(
        IGpuVariantSelector variantSelector,
        IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppBinaryManager binaryManager,
        ILlamaCppSourceBuildActivity buildActivity,
        ILlamaCppSourceBuildPrerequisiteProbe sourceBuildPrerequisiteProbe,
        ILlamaCppSourceBuildService sourceBuildService,
        ILlamaCppUpdateState updateState,
        ILlamaServerProcessSupervisor supervisor,
        IRuntimeAcquisitionStatusRegistry acquisitionStatus)
    {
        ArgumentNullException.ThrowIfNull(acquisitionStatus);
        ArgumentNullException.ThrowIfNull(binaryManager);
        ArgumentNullException.ThrowIfNull(buildActivity);
        ArgumentNullException.ThrowIfNull(installedRuntimeStore);
        ArgumentNullException.ThrowIfNull(sourceBuildPrerequisiteProbe);
        ArgumentNullException.ThrowIfNull(sourceBuildService);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(updateState);
        ArgumentNullException.ThrowIfNull(variantSelector);
        _acquisitionStatus = acquisitionStatus;
        _binaryManager = binaryManager;
        _buildActivity = buildActivity;
        _installedRuntimeStore = installedRuntimeStore;
        _sourceBuildPrerequisiteProbe = sourceBuildPrerequisiteProbe;
        _sourceBuildService = sourceBuildService;
        _supervisor = supervisor;
        _updateState = updateState;
        _variantSelector = variantSelector;
    }

    /// <summary>The last observed llama.cpp update/runtime snapshot, which a runtime-status response is rendered from.</summary>
    public LlamaCppUpdateSnapshot CurrentUpdateSnapshot => _updateState.Current;

    /// <summary>The source-build toolchain checklist for one backend, so a refusal can name which prerequisite is missing.</summary>
    public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeSourceBuildPrerequisitesAsync(LlamaCppSourceBackend backend, CancellationToken ct)
    {
        return _sourceBuildPrerequisiteProbe.ProbeAsync(backend, ct);
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

    /// <summary>
    ///     Provisions the <c>(model, role)</c> process and returns its loopback endpoint, spawning one if none is warm.
    ///     Verbatim pass-through: every refusal, cap and backoff decision stays in the supervisor, and a failure still
    ///     surfaces as the supervisor's own <c>LlamaRuntimeException</c>.
    /// </summary>
    public Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        return _supervisor.EnsureRunningAsync(modelName, role, ct);
    }

    /// <summary>
    ///     Takes a reference-counted inference lease against the currently-running <c>(model, role)</c> process so a
    ///     graceful eject drains the request instead of killing it mid-flight. Verbatim pass-through: the caller reads
    ///     the three outcomes off the returned acquisition and MUST dispose a granted lease.
    /// </summary>
    public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role)
    {
        return _supervisor.TryAcquireInferenceLease(modelName, role);
    }

    /// <summary>How many <c>llama-server</c> processes are alive — what an eject-first refusal reports back.</summary>
    public int CountRunningProcesses()
    {
        return _supervisor.CountRunningProcesses();
    }

    /// <summary>
    ///     Probes the host and selects the llama.cpp prebuilt variant to download. Verbatim pass-through: the whole
    ///     GPU-detection policy stays in the selector.
    /// </summary>
    public Task<GpuVariant> SelectGpuVariantAsync(CancellationToken ct)
    {
        return _variantSelector.SelectVariantAsync(ct);
    }

    /// <summary>
    ///     Ensures a hash-verified <c>llama-server</c> binary for <paramref name="variant" /> is on disk and returns its
    ///     location. Verbatim pass-through: idempotency, re-download and the sanitized <c>LlamaRuntimeException</c> all
    ///     stay in the binary manager.
    /// </summary>
    public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct)
    {
        return _binaryManager.EnsureBinaryAsync(variant, ct);
    }

    /// <summary>
    ///     Records one runtime-acquisition status under a freshly-stamped sequence and broadcasts it. Verbatim
    ///     pass-through: the registry owns the sequence and the push throttle, and the call stays non-throwing.
    /// </summary>
    public void ReportRuntimeAcquisition(RuntimeAcquisitionUpdate update)
    {
        _acquisitionStatus.Report(update);
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

    private static bool IsSourceBuildActive(ILlamaCppSourceBuildActivity sourceBuildActivity)
    {
        return sourceBuildActivity.ActiveBuildId is not null;
    }

    /// <summary>
    ///     Shared remove gate for the managed source-build runtime. Refuses while a source build is
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

        await using var mutationLease = await processSupervisor.TryAcquireRuntimeMutationLeaseAsync(ct);
        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return new LlamaCppRuntimeRemovalOutcome(Removed: false, RunningProcessCount: 0, BuildActive: true);
        }

        var runningProcessCount = processSupervisor.CountRunningProcesses();
        if (mutationLease is null || runningProcessCount > 0)
        {
            return new LlamaCppRuntimeRemovalOutcome(Removed: false, runningProcessCount, BuildActive: false);
        }

        await removeAsync(ct);
        return new LlamaCppRuntimeRemovalOutcome(Removed: true, runningProcessCount, BuildActive: false);
    }
}

/// <summary>
///     The result of the shared remove gate: whether the removal ran, how many llama-server processes were still
///     running when the gate was evaluated, and whether a source build blocked it.
/// </summary>
public sealed record LlamaCppRuntimeRemovalOutcome(bool Removed, int RunningProcessCount, bool BuildActive);
