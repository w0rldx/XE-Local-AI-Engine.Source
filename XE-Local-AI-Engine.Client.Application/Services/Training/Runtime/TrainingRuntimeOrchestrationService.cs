namespace XE_Local_AI_Engine.Client.Services.Training.Runtime;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     The Training runtime Operator endpoints' only door onto the provider's runtime service and prerequisite
///     probe: the status read, the prerequisite report, and the install / cancel / remove verbs.
/// </summary>
/// <remarks>
///     An endpoint is the HTTP edge and may not take a concrete provider's contract itself (the endpoint-dependency rule), so
///     each call arrives here unchanged, except that install and remove carry the GPU-job admission check the provider runs
///     inside its start transaction. The interpreter-path lookup the run executor and the export service use stays off this
///     surface deliberately: no endpoint asks for it.
/// </remarks>
public sealed class TrainingRuntimeOrchestrationService
{
    private readonly IGpuWorkGate _gpuWorkGate;
    private readonly IGpuIdleHolderPreemption _idlePreemption;
    private readonly IModelLabActiveJob _labJob;
    private readonly ITrainingRuntimeService _runtime;
    private readonly ITrainingRuntimePrerequisiteProbe _prerequisiteProbe;
    private readonly ILlamaServerProcessSupervisor _supervisor;

    public TrainingRuntimeOrchestrationService(ITrainingRuntimeService runtime,
        ITrainingRuntimePrerequisiteProbe prerequisiteProbe,
        IGpuWorkGate gpuWorkGate,
        IGpuIdleHolderPreemption idlePreemption,
        ILlamaServerProcessSupervisor supervisor,
        IModelLabActiveJob labJob)
    {
        ArgumentNullException.ThrowIfNull(gpuWorkGate);
        ArgumentNullException.ThrowIfNull(idlePreemption);
        ArgumentNullException.ThrowIfNull(labJob);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(prerequisiteProbe);
        _gpuWorkGate = gpuWorkGate;
        _idlePreemption = idlePreemption;
        _labJob = labJob;
        _runtime = runtime;
        _prerequisiteProbe = prerequisiteProbe;
        _supervisor = supervisor;
    }

    /// <summary>True while a job running the training runtime's Python owns the node: it runs from the directory a remove or an install swaps.</summary>
    /// <remarks>
    ///     An evaluation runs on llama-server instead. The lease tells a running job from the queue, which holds the gate for a moment on every
    ///     poll while a run waits behind a loaded model. An idle Model Lab worker is evictable, not busy: the admission check evicts it first.
    /// </remarks>
    public bool IsGpuJobRunning =>
        _gpuWorkGate.ExclusiveKind is GpuWorkKind.TrainingRun or GpuWorkKind.Export or GpuWorkKind.ModelLabExperiment
        && _supervisor.IsKeepWarmSuppressed()
        && !_idlePreemption.IsIdlePreemptible;

    /// <summary>The routes' pre-check: <see cref="IsGpuJobRunning" />, after joining a Model Lab eviction already under way; a refusal latches the demand first.</summary>
    /// <remarks>
    ///     The wait returns at once for a busy holder. The check is read again after it, since another holder may own the gate by
    ///     then. A refusal that raised no demand would let an active Model Lab worker go idle and keep running from the directory the
    ///     retry swaps.
    /// </remarks>
    public async Task<bool> RefusesForRunningGpuJobAsync(CancellationToken ct)
    {
        if (!IsGpuJobRunning)
        {
            return false;
        }

        _ = await _idlePreemption.WaitForReleaseAsync(GpuWorkAdmission.IdleHolderReleaseBound, ct);
        if (!IsGpuJobRunning)
        {
            return false;
        }

        _idlePreemption.RequestPreemption();
        return true;
    }

    /// <summary>
    ///     Reports whether this machine can provision the Python training runtime. Read-only: probing never mutates
    ///     the cache root, so the UI may call it freely before the operator commits to a multi-gigabyte install.
    /// </summary>
    public Task<SourceBuildPrerequisiteReport> ProbeAsync(CancellationToken ct)
    {
        return _prerequisiteProbe.ProbeAsync(ct);
    }

    /// <summary>The current phase, log tail and installed-state record of the single-flight runtime install.</summary>
    public TrainingRuntimeStatus GetStatus()
    {
        return _runtime.GetStatus();
    }

    /// <summary>Starts a runtime install, or reports the refusal that stopped it from starting.</summary>
    public Task<TrainingRuntimeInstallResult> InstallAsync(CancellationToken ct)
    {
        return _runtime.InstallAsync(AdmitAsync, ct);
    }

    /// <summary>Removes the installed runtime and its state record. Refused while an install or a GPU job is in flight.</summary>
    public Task<TrainingRuntimeRemoveOutcome> RemoveAsync(CancellationToken ct)
    {
        return _runtime.RemoveAsync(AdmitAsync, ct);
    }

    /// <summary>Requests cancellation of an in-flight install. Returns false when nothing is running.</summary>
    public bool Cancel()
    {
        return _runtime.Cancel();
    }

    /// <summary>The authoritative check, run by the provider after it published its non-ready phase: evicts an idle Model Lab worker, then admits unless a job runs.</summary>
    /// <remarks>
    ///     The demand is raised unconditionally, so a worker active now cannot fall back to idle and keep running from the
    ///     directory this install or remove swaps; a busy one stays busy and refuses. A worker not seen to exit gave the gate and
    ///     the lease back but may still run from that directory, so it refuses too until a reap proves it gone.
    /// </remarks>
    private async Task<bool> AdmitAsync(CancellationToken ct)
    {
        _ = await _idlePreemption.WaitForReleaseAsync(GpuWorkAdmission.IdleHolderReleaseBound, ct);
        return !IsGpuJobRunning && !await _labJob.HasUnconfirmedWorkerAsync(ct);
    }
}
