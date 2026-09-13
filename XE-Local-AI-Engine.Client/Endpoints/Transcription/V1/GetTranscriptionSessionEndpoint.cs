namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Reads one session with its transcript and the options it ran under; 404 when the id is unknown. The transcript
///     text is decrypted here and returned only to the authenticated operator, and is never logged. Operator-gated.
/// </summary>
public sealed class GetTranscriptionSessionEndpoint(ITranscriptionService sessions)
    : Endpoint<TranscriptionSessionRouteRequest, TranscriptionSessionDetailResponse>
{
    private readonly ITranscriptionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public override void Configure()
    {
        Get(LocalApiRoutes.Transcription.SessionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<TranscriptionSessionDetailResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(TranscriptionSessionRouteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var session = await _sessions.GetSessionAsync(req.SessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(session.ToResponse(), ct).ConfigureAwait(false);
    }
}
