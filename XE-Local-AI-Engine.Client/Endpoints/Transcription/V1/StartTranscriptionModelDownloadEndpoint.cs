namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Begins a weight download and returns 202 immediately. The transfer outlives this request, so its outcome —
///     including failure — is observed through the model list rather than through this response. Operator-gated.
/// </summary>
public sealed class StartTranscriptionModelDownloadEndpoint(IWhisperModelDownloadCoordinator downloadCoordinator)
    : Endpoint<TranscriptionModelDownloadRequest, TranscriptionModelDownloadResponse>
{
    private readonly IWhisperModelDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.ModelDownloads);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<TranscriptionModelDownloadRequest>("application/json")
                               .Produces<TranscriptionModelDownloadResponse>(StatusCodes.Status202Accepted));
    }

    public override async Task HandleAsync(TranscriptionModelDownloadRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The request token is deliberately not involved: it is cancelled the instant the 202 is written, while the
        // download outlives this request.
        var ticket = _downloadCoordinator.Start(request.ModelId);

        await Send.ResultAsync(Results.Accepted(uri: null, new TranscriptionModelDownloadResponse
        {
            ModelId = ticket.ModelId,
            Accepted = true,
            AlreadyInFlight = ticket.AlreadyInFlight,
            Status = _downloadCoordinator.GetStatus(ticket.ModelId)?.ToResponse()
        })).ConfigureAwait(false);
    }
}
