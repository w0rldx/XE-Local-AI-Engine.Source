namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     The transcription runtime and source-build Operator endpoints' only door onto the whisper.cpp provider: the
///     backend selection behind the model recommendation, the prerequisite checklist, the start / cancel / remove /
///     status verbs of the managed source build, and the activity snapshot a <c>409 runtime-busy</c> envelope carries.
///     An endpoint is the HTTP edge and may not take a concrete provider's contract itself (the endpoint-dependency
///     rule), so each call arrives here unchanged — this type adds no policy of its own, decides nothing about
///     refusals, outcomes or the OS gate, and re-exposes only the members those six endpoints call. The host-start
///     recovery and shutdown drain of the build service, and the activity gate's leases, stay off this surface
///     deliberately: no endpoint asks for them.
/// </summary>
public sealed class WhisperRuntimeOrchestrationService(
    IWhisperCppSourceBuildService buildService,
    IWhisperCppSourceBuildPrerequisiteProbe prerequisiteProbe,
    IWhisperRuntimeActivityGate activityGate,
    IWhisperBackendSelector backendSelector)
{
    private readonly IWhisperRuntimeActivityGate _activityGate = activityGate ?? throw new ArgumentNullException(nameof(activityGate));

    private readonly IWhisperBackendSelector _backendSelector = backendSelector ?? throw new ArgumentNullException(nameof(backendSelector));

    private readonly IWhisperCppSourceBuildService _buildService = buildService ?? throw new ArgumentNullException(nameof(buildService));

    private readonly IWhisperCppSourceBuildPrerequisiteProbe _prerequisiteProbe =
        prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));

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
