namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Cancels an in-flight weight download. Idempotent: cancelling one that just finished reports no change rather
///     than failing, because the operator clicking a stale row is a race, not a mistake. A 1.6 GB pull that could not
///     be stopped would hold the node's bandwidth and disk until it finished. Operator-gated.
/// </summary>
public sealed class CancelTranscriptionModelDownloadEndpoint : Endpoint<TranscriptionModelDownloadRequest, TranscriptionModelDownloadResponse>
{
    private readonly IWhisperModelDownloadCoordinator _downloadCoordinator;

    public CancelTranscriptionModelDownloadEndpoint(IWhisperModelDownloadCoordinator downloadCoordinator)
    {
        ArgumentNullException.ThrowIfNull(downloadCoordinator);
        _downloadCoordinator = downloadCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.ModelDownloadCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<TranscriptionModelDownloadRequest>("application/json")
                               .Produces<TranscriptionModelDownloadResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(TranscriptionModelDownloadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var cancelled = _downloadCoordinator.Cancel(request.ModelId);

        await Send.OkAsync(new TranscriptionModelDownloadResponse
        {
            ModelId = request.ModelId,
            Accepted = cancelled,
            Status = _downloadCoordinator.GetStatus(request.ModelId)?.ToResponse()
        }, ct);
    }
}
