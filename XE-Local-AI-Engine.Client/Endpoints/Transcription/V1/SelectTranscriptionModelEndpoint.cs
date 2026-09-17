namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Sets or clears the operator's model choice and returns the refreshed catalogue. A null or blank id clears the
///     override, so the node falls back to the hardware recommendation. Operator-gated.
/// </summary>
public sealed class SelectTranscriptionModelEndpoint(ITranscriptionRuntimeService runtimeService)
    : Endpoint<SelectTranscriptionModelRequest, TranscriptionModelListResponse>
{
    private readonly ITranscriptionRuntimeService _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.ModelSelect);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<SelectTranscriptionModelRequest>("application/json")
                               .Produces<TranscriptionModelListResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(SelectTranscriptionModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var catalog = await _runtimeService.SelectModelAsync(request.ModelId, ct);
        await Send.OkAsync(catalog.ToResponse(), ct);
    }
}
