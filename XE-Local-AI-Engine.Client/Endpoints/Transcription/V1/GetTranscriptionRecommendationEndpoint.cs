namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Reports which model this node's hardware should run, and the footprint figures behind the answer, so the
///     operator sees the reasoning rather than an unexplained pick. Operator-gated.
/// </summary>
public sealed class GetTranscriptionRecommendationEndpoint(
    ITranscriptionRuntimeService runtimeService,
    IWhisperBackendSelector backendSelector) : EndpointWithoutRequest<TranscriptionModelRecommendationResponse>
{
    private readonly IWhisperBackendSelector _backendSelector = backendSelector ?? throw new ArgumentNullException(nameof(backendSelector));
    private readonly ITranscriptionRuntimeService _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.RuntimeRecommendation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<TranscriptionModelRecommendationResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var entry = await _runtimeService.GetRecommendedModelAsync(ct).ConfigureAwait(false);
        var backend = await _backendSelector.SelectBackendAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(entry.ToRecommendationResponse(backend), ct).ConfigureAwait(false);
    }
}
