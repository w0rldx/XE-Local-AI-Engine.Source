namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     Stops server-side per-application capture for a session: 204 when one was stopped, 404 when that session was
///     not capturing. Operator-gated.
/// </summary>
/// <remarks>
///     This ends the <b>capture</b>, not the session. A live session still ends through its single termination path
///     in the registry, which the cancel route reaches; this endpoint is a convenience over that path and never an
///     alternative to it.
/// </remarks>
public sealed class StopProcessCaptureEndpoint(ProcessAudioCaptureCoordinator captures)
    : Endpoint<TranscriptionSessionRouteRequest>
{
    private readonly ProcessAudioCaptureCoordinator _captures = captures ?? throw new ArgumentNullException(nameof(captures));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Transcription.SessionProcessCapture);
        Policies(NodeAuthorizationPolicies.Operator);

        // Route-only DELETE (session id from the route, no body): override the default application/json-only
        // Accepts so a body-less request is not rejected with 415, exactly as the cancel route does.
        Description(builder => builder
                               .Accepts<TranscriptionSessionRouteRequest>()
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(TranscriptionSessionRouteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (!await _captures.StopAsync(req.SessionId, ct).ConfigureAwait(false))
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
