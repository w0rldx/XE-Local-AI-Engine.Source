namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Starts live capture for an existing session: the row moves to <c>Transcribing</c> and its lanes are
///     registered, so the hub will accept audio for it. Operator-gated.
/// </summary>
/// <remarks>
///     <para>
///         The client awaits this before it forwards a single frame — without it the hub refuses every push, since a
///         session with no lanes has nowhere to put audio. It is idempotent: a retry, a double-click or a reconnect
///         that re-issues the start answers 200 with the same state rather than registering a second set of lanes.
///     </para>
///     <para>
///         404 for an unknown session, 409 for one that already finished (with the terminal status in the body, so
///         the client can say which), and 400 for a session whose source kind has no live capture path — a file
///         session, where retrying could never work.
///     </para>
/// </remarks>
public sealed class StartLiveTranscriptionSessionEndpoint(ITranscriptionService sessions)
    : Endpoint<TranscriptionSessionRouteRequest, StartLiveTranscriptionSessionResponse>
{
    private readonly ITranscriptionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public override void Configure()
    {
        Post(LocalApiRoutes.Transcription.SessionLiveStart);
        Policies(NodeAuthorizationPolicies.Operator);

        // Route-only POST (session id from the route, no body): override the default application/json-only Accepts so
        // a body-less request is not rejected with 415, exactly as the cancel route does.
        Description(builder => builder
                               .Accepts<TranscriptionSessionRouteRequest>()
                               .Produces<StartLiveTranscriptionSessionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<StartLiveTranscriptionSessionResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(TranscriptionSessionRouteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        StartLiveResult result;
        try
        {
            result = await _sessions.StartLiveAsync(req.SessionId, ct).ConfigureAwait(false);
        }
        catch (LiveTranscriptionSourceKindException exception)
        {
            // A refusal about the session's own shape, not about anything the caller sent — but it is still the
            // caller's request that cannot be satisfied, and a 500 would invite a retry that can never work.
            AddError(exception.Message);
            await Send.ErrorsAsync(StatusCodes.Status400BadRequest, ct).ConfigureAwait(false);
            return;
        }

        if (result.Outcome == StartLiveOutcome.SessionNotFound)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        if (result.Outcome == StartLiveOutcome.SessionAlreadyFinished)
        {
            await Send.ResultAsync(Results.Conflict(ToResponse(req.SessionId, result))).ConfigureAwait(false);
            return;
        }

        // Started and AlreadyLive are the same answer to the caller: the session is live and this is where to resume.
        await Send.OkAsync(ToResponse(req.SessionId, result), ct).ConfigureAwait(false);
    }

    private static StartLiveTranscriptionSessionResponse ToResponse(Guid sessionId, StartLiveResult result) =>
        new()
        {
            SessionId = sessionId,
            Status = result.Status.ToString(),
            LastSeq = result.LastSeq
        };
}
