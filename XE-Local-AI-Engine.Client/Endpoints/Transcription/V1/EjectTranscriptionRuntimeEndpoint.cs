namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Tree-kills the resident <c>whisper-server</c> daemon to free its memory. Refuses with <c>409 runtime-busy</c>
///     while a transcription or a spawn is in flight, carrying the activity snapshot so the operator can see what to
///     wait for rather than being told only that it failed. Operator-gated.
/// </summary>
public sealed class EjectTranscriptionRuntimeEndpoint : Endpoint<TranscriptionRuntimeActionRequest, TranscriptionRuntimeStatusResponse>
{
    private readonly ITranscriptionRuntimeService _runtimeService;

    public EjectTranscriptionRuntimeEndpoint(ITranscriptionRuntimeService runtimeService)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        _runtimeService = runtimeService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.RuntimeEject);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<TranscriptionRuntimeActionRequest>("application/json")
                               .Produces<TranscriptionRuntimeStatusResponse>(StatusCodes.Status200OK)
                               .Produces<TranscriptionRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(TranscriptionRuntimeActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _runtimeService.EjectAsync(ct);
        if (!result.Evicted)
        {
            await Send.ResultAsync(TranscriptionRuntimeBlockedEndpointSupport.RuntimeBusy(
                          "Wait for the running transcription, runtime startup, or runtime mutation to finish before ejecting the transcription runtime.",
                          result.Activity));
            return;
        }

        var view = await _runtimeService.GetRuntimeAsync(ct);
        await Send.OkAsync(view.ToResponse(), ct);
    }
}
