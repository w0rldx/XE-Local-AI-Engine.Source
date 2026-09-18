namespace XE_Local_AI_Engine.Client.Services.Training.Runtime;

using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     The Training runtime Operator endpoints' only door onto the provider's runtime service and prerequisite
///     probe: the status read, the prerequisite report, and the install / cancel / remove verbs. An endpoint is the
///     HTTP edge and may not take a concrete provider's contract itself (the endpoint-dependency rule), so each call
///     arrives here unchanged — this type adds no policy of its own, decides nothing about install outcomes or
///     refusals, and re-exposes only the members those four endpoints call. The interpreter-path lookup the run
///     executor and the export service use stays off this surface deliberately: no endpoint asks for it.
/// </summary>
public sealed class TrainingRuntimeOrchestrationService
{
    private readonly ITrainingRuntimeService _runtime;
    private readonly ITrainingRuntimePrerequisiteProbe _prerequisiteProbe;

    public TrainingRuntimeOrchestrationService(ITrainingRuntimeService runtime, ITrainingRuntimePrerequisiteProbe prerequisiteProbe)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(prerequisiteProbe);
        _runtime = runtime;
        _prerequisiteProbe = prerequisiteProbe;
    }

    /// <summary>
    ///     Reports whether this machine can provision the Python training runtime. Read-only: probing never mutates
    ///     the cache root, so the UI may call it freely before the operator commits to a multi-gigabyte install.
    /// </summary>
    public Task<TrainingRuntimePrerequisiteReport> ProbeAsync(CancellationToken ct)
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
        return _runtime.InstallAsync(ct);
    }

    /// <summary>Removes the installed runtime and its state record. Refused while an install is in flight.</summary>
    public Task<bool> RemoveAsync(CancellationToken ct)
    {
        return _runtime.RemoveAsync(ct);
    }

    /// <summary>Requests cancellation of an in-flight install. Returns false when nothing is running.</summary>
    public bool Cancel()
    {
        return _runtime.Cancel();
    }
}
