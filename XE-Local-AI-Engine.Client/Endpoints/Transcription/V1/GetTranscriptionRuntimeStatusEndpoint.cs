namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Reports the transcription runtime: its state, the resolved binary, the selected and recommended models, the
///     managed-runtime record and what is currently holding it. Operator-gated. No path, URL or port is surfaced.
/// </summary>
public sealed class GetTranscriptionRuntimeStatusEndpoint(ITranscriptionRuntimeService runtimeService)
    : EndpointWithoutRequest<TranscriptionRuntimeStatusResponse>
{
    private readonly ITranscriptionRuntimeService _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.Runtime);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<TranscriptionRuntimeStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var view = await _runtimeService.GetRuntimeAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(view.ToResponse(), ct).ConfigureAwait(false);
    }
}
