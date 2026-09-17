namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Requests cancellation of a running source build and returns the status that results. Cancelling when nothing
///     is running is a success, not an error — the caller asked for a state that already holds. Operator-gated.
/// </summary>
public sealed class CancelWhisperCppSourceBuildEndpoint(WhisperRuntimeOrchestrationService whisperRuntime)
    : Endpoint<TranscriptionRuntimeActionRequest, WhisperCppSourceBuildStatusResponse>
{
    private readonly WhisperRuntimeOrchestrationService _whisperRuntime =
        whisperRuntime ?? throw new ArgumentNullException(nameof(whisperRuntime));

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.RuntimeSourceBuildCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<TranscriptionRuntimeActionRequest>("application/json")
                               .Produces<WhisperCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(TranscriptionRuntimeActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = _whisperRuntime.Cancel();
        await Send.OkAsync(_whisperRuntime.GetStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
