namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     Starts live capture for an existing session: the row moves to <c>Transcribing</c> and its lanes are
///     registered, so the hub will accept audio for it. Operator-gated.
/// </summary>
/// <remarks>
///     The client awaits this before its first frame: a session with no lanes makes the hub refuse every push. Idempotent:
///     a retry or reconnect answers 200 with the same state and never registers a second set of lanes. 404 for an unknown
///     session, 409 for a finished one (terminal status in the body), 400 for a source kind with no live capture path or a
///     whisper runtime that could not be warmed (the row is left untouched).
/// </remarks>
public sealed class StartLiveTranscriptionSessionEndpoint : Endpoint<TranscriptionSessionRouteRequest, StartLiveTranscriptionSessionResponse>
{
    private readonly ITranscriptionService _sessions;

    public StartLiveTranscriptionSessionEndpoint(ITranscriptionService sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

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
            result = await _sessions.StartLiveAsync(req.SessionId, ct);
        }
        catch (LiveTranscriptionSourceKindException exception)
        {
            // A refusal about the session's own shape, not about anything the caller sent — but it is still the
            // caller's request that cannot be satisfied, and a 500 would invite a retry that can never work.
            AddError(exception.Message);
            await Send.ErrorsAsync(StatusCodes.Status400BadRequest, ct);
            return;
        }
        catch (WhisperRuntimeException exception)
        {
            // The pre-warm failed before the row moved. The message is contractually sanitized, so it is the answer, as
            // on the upload path, instead of a bare 500 from the global handler.
            AddError(exception.Message);
            await Send.ErrorsAsync(StatusCodes.Status400BadRequest, ct);
            return;
        }

        if (result.Outcome == StartLiveOutcome.SessionNotFound)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (result.Outcome == StartLiveOutcome.SessionAlreadyFinished)
        {
            await Send.ResultAsync(Results.Conflict(ToResponse(req.SessionId, result)));
            return;
        }

        // Started and AlreadyLive are the same answer to the caller: the session is live and this is where to resume.
        await Send.OkAsync(ToResponse(req.SessionId, result), ct);
    }

    private static StartLiveTranscriptionSessionResponse ToResponse(Guid sessionId, StartLiveResult result) =>
        new()
        {
            SessionId = sessionId,
            Status = result.Status.ToString(),
            LastSeq = result.LastSeq
        };
}
