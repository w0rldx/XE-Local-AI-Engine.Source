namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Cancels any transcription still running for the session, then deletes the session and its transcript; 404 when
///     the id is unknown. There is no audio to delete — the uploaded bytes were removed when the upload that produced
///     the transcript ended. Operator-gated.
/// </summary>
public sealed class DeleteTranscriptionSessionEndpoint(ITranscriptionService sessions)
    : Endpoint<TranscriptionSessionRouteRequest>
{
    private readonly ITranscriptionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Transcription.SessionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(TranscriptionSessionRouteRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var deleted = await _sessions.DeleteSessionAsync(req.SessionId, ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
