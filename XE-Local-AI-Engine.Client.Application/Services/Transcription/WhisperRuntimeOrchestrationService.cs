namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     The transcription runtime and source-build Operator endpoints' only door onto the whisper.cpp provider:
///     backend selection, the prerequisite checklist, and the managed source build's verbs.
/// </summary>
/// <remarks>
///     It also carries the backend selection behind the model recommendation, the start / cancel / remove / status verbs, and
///     the activity snapshot a <c>409 runtime-busy</c> envelope carries. An endpoint is the HTTP edge and may not take a
///     concrete provider's contract itself (the endpoint-dependency rule), so each call arrives here unchanged: this type adds
///     no policy, decides nothing about refusals, outcomes or the OS gate, and re-exposes only what those six endpoints call.
///     The build service's host-start recovery and shutdown drain, and the activity gate's leases, stay off deliberately.
/// </remarks>
public sealed class WhisperRuntimeOrchestrationService
{
    private readonly IWhisperRuntimeActivityGate _activityGate;

    private readonly IWhisperBackendSelector _backendSelector;

    private readonly IWhisperCppSourceBuildService _buildService;
    private readonly IWhisperCppSourceBuildPrerequisiteProbe _prerequisiteProbe;

    public WhisperRuntimeOrchestrationService(
        IWhisperCppSourceBuildService buildService,
        IWhisperCppSourceBuildPrerequisiteProbe prerequisiteProbe,
        IWhisperRuntimeActivityGate activityGate,
        IWhisperBackendSelector backendSelector)
    {
        ArgumentNullException.ThrowIfNull(activityGate);
        ArgumentNullException.ThrowIfNull(backendSelector);
        ArgumentNullException.ThrowIfNull(buildService);
        ArgumentNullException.ThrowIfNull(prerequisiteProbe);
        _activityGate = activityGate;
        _backendSelector = backendSelector;
        _buildService = buildService;
        _prerequisiteProbe = prerequisiteProbe;
    }

    /// <summary>Returns the active runtime backend, or probes the host to select an exact prebuilt backend.</summary>
    public Task<WhisperBackend> SelectBackendAsync(CancellationToken ct)
    {
        return _backendSelector.SelectBackendAsync(ct);
    }

    /// <summary>The toolchain checklist for one backend, so a refusal can name which prerequisite is missing.</summary>
    public Task<WhisperCppSourceBuildPrerequisiteReport> ProbeAsync(WhisperBackend backend, CancellationToken ct)
    {
        return _prerequisiteProbe.ProbeAsync(backend, ct);
    }

    /// <summary>Validates the request, checks prerequisites, and detaches the build. Returns as soon as it starts.</summary>
    public Task<WhisperCppSourceBuildStartResult> StartAsync(WhisperCppSourceBuildRequest request, CancellationToken ct)
    {
        return _buildService.StartAsync(request, ct);
    }

    /// <summary>Deletes the adopted managed runtime and its record. The in-app recovery from a fail-closed tombstone.</summary>
    public Task<WhisperCppSourceBuildRemoveResult> RemoveAsync(CancellationToken ct)
    {
        return _buildService.RemoveAsync(ct);
    }

    /// <summary>The current snapshot; never blocks on the build.</summary>
    public WhisperCppSourceBuildStatus GetStatus()
    {
        return _buildService.GetStatus();
    }

    /// <summary>Requests cancellation of a running build. False when there is nothing to cancel.</summary>
    public bool Cancel()
    {
        return _buildService.Cancel();
    }

    /// <summary>
    ///     A process-wide snapshot of transcription-runtime activity, which is what a runtime-busy refusal reports so
    ///     the operator is told what to wait for.
    /// </summary>
    public WhisperRuntimeActivitySnapshot GetActivitySnapshot()
    {
        return _activityGate.GetSnapshot();
    }
}
