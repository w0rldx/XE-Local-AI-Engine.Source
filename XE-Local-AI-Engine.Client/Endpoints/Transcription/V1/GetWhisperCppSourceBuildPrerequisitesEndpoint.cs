namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Reports the toolchain checklist for a backend, so the operator sees which prerequisite is missing instead of
///     only that one is. Operator-gated.
/// </summary>
public sealed class GetWhisperCppSourceBuildPrerequisitesEndpoint : Endpoint<GetWhisperCppSourceBuildPrerequisitesRequest, WhisperCppSourceBuildPrerequisitesResponse>
{
    private readonly WhisperRuntimeOrchestrationService _whisperRuntime;

    public GetWhisperCppSourceBuildPrerequisitesEndpoint(WhisperRuntimeOrchestrationService whisperRuntime)
    {
        ArgumentNullException.ThrowIfNull(whisperRuntime);
        _whisperRuntime = whisperRuntime;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.RuntimeSourceBuildPrerequisites);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<WhisperCppSourceBuildPrerequisitesResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(GetWhisperCppSourceBuildPrerequisitesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var backend = request.Backend.ToContract();
        var report = await _whisperRuntime.ProbeAsync(backend, ct);
        await Send.OkAsync(report.ToResponse(backend), ct);
    }
}
