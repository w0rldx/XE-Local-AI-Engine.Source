namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Signals the transcription running for this session; 204 when one was signalled, 404 when nothing is in flight
///     for that id. Cancellation is a signal, not a join: the run unwinds on its own and writes the session
///     <c>Cancelled</c>. Operator-gated.
/// </summary>
public sealed class CancelTranscriptionSessionEndpoint : Endpoint<TranscriptionSessionRouteRequest>
{
    private readonly ITranscriptionService _sessions;

    public CancelTranscriptionSessionEndpoint(ITranscriptionService sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.SessionCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST (session id from the route, no body): override the default application/json-only Accepts so
        // a body-less request is not rejected with 415 (see CancelImageJobEndpoint for the full rationale).
        Description(builder => builder
                               .Accepts<TranscriptionSessionRouteRequest>()
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(TranscriptionSessionRouteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var cancelled = await _sessions.CancelAsync(req.SessionId, ct);
        if (!cancelled)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
