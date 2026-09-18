namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     The poll route for a running or last-finished source build: its phase, a bounded sanitized log tail, and the
///     descriptor of what is being built. Operator-gated.
/// </summary>
public sealed class GetWhisperCppSourceBuildStatusEndpoint : EndpointWithoutRequest<WhisperCppSourceBuildStatusResponse>
{
    private readonly WhisperRuntimeOrchestrationService _whisperRuntime;

    public GetWhisperCppSourceBuildStatusEndpoint(WhisperRuntimeOrchestrationService whisperRuntime)
    {
        ArgumentNullException.ThrowIfNull(whisperRuntime);
        _whisperRuntime = whisperRuntime;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.RuntimeSourceBuildStatus);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<WhisperCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_whisperRuntime.GetStatus().ToResponse(), ct);
    }
}
