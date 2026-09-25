namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

using System.Diagnostics;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Replaces one transcript row's text and answers the row as it now reads. Operator-gated.
/// </summary>
/// <remarks>
///     404 when the session or the row is unknown; 409 with a reason code while the session is still transcribing,
///     because a live or draining session is still committing rows and an edit would race them. The text is stored
///     trimmed, encrypted like every other transcript row, and never logged.
/// </remarks>
public sealed class UpdateTranscriptSegmentEndpoint : Endpoint<UpdateTranscriptSegmentRequest, TranscriptSegmentResponse>
{
    /// <summary>The session is live, draining or running a batch job; wait for it to finish.</summary>
    internal const string SessionTranscribingReason = "session-transcribing";

    private readonly ITranscriptionService _sessions;

    public UpdateTranscriptSegmentEndpoint(ITranscriptionService sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Transcription.SessionSegmentBySeq);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<TranscriptSegmentResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<TranscriptSegmentUpdateBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateTranscriptSegmentRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var result = await _sessions.UpdateSegmentTextAsync(req.SessionId, req.Seq, req.Text.Trim(), ct);
        switch (result.Outcome)
        {
            case UpdateTranscriptSegmentOutcome.Updated when result.Segment is not null:
                await Send.OkAsync(result.Segment.ToResponse(), ct);
                return;

            case UpdateTranscriptSegmentOutcome.SessionNotFound:
            case UpdateTranscriptSegmentOutcome.SegmentNotFound:
                await Send.NotFoundAsync(ct);
                return;

            case UpdateTranscriptSegmentOutcome.SessionTranscribing:
                await Send.ResultAsync(Results.Conflict(new TranscriptSegmentUpdateBlockedResponse
                {
                    Reason = SessionTranscribingReason,
                    Message = "This session is still transcribing. Edit its transcript once it has finished."
                }));
                return;

            default:
                throw new UnreachableException($"Unknown transcript-edit outcome '{result.Outcome}'.");
        }
    }
}
