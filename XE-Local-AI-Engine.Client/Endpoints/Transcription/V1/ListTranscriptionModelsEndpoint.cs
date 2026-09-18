namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Lists the whisper weight catalogue with what this node knows about each row: whether it is installed, any
///     download in flight, and which ids are selected and recommended. Operator-gated; no absolute path is surfaced.
/// </summary>
public sealed class ListTranscriptionModelsEndpoint : EndpointWithoutRequest<TranscriptionModelListResponse>
{
    private readonly ITranscriptionRuntimeService _runtimeService;

    public ListTranscriptionModelsEndpoint(ITranscriptionRuntimeService runtimeService)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        _runtimeService = runtimeService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.Models);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<TranscriptionModelListResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var catalog = await _runtimeService.GetModelsAsync(ct);
        await Send.OkAsync(catalog.ToResponse(), ct);
    }
}
