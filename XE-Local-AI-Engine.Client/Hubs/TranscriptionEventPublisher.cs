namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Pushes what a live session produced to that session's group. Supersedes the no-op the application module
///     registers, so a host without this hub stays resolvable.
/// </summary>
/// <remarks>
///     Every enum is mapped to its wire spelling here rather than crossing the seam as a string, so renaming a member
///     of <see cref="TranscriptChannel" /> or <see cref="LiveEndReason" /> cannot change the contract by accident.
///     The channel spelling is the mapper's, shared with the REST transcript rows: a resuming client merges the
///     replay snapshot and the live pushes into one list, so the same field must be spelled the same way in both.
/// </remarks>
internal sealed class TranscriptionEventPublisher : ITranscriptionEventPublisher
{
    private readonly IHubContext<TranscriptionHub> _hubContext;

    public TranscriptionEventPublisher(IHubContext<TranscriptionHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public Task PublishSegmentAsync(Guid sessionId,
        long seq,
        TranscriptChannel channel,
        long startMs,
        long endMs,
        string text,
        double? confidence,
        CancellationToken cancellationToken) =>
        _hubContext.Clients
                   .Group(TranscriptionHubGroups.Session(sessionId))
                   .SendAsync(TranscriptionHubEvents.SegmentCommitted,
                       new TranscriptSegmentCommittedPush
                       {
                           SessionId = sessionId,
                           Seq = seq,
                           StartMs = startMs,
                           EndMs = endMs,
                           Text = text,
                           Channel = TranscriptionMapper.ToWireChannel(channel),
                           Confidence = confidence
                       },
                       cancellationToken);

    public Task PublishPartialAsync(Guid sessionId, TranscriptChannel channel, string text, CancellationToken cancellationToken) =>
        _hubContext.Clients
                   .Group(TranscriptionHubGroups.Session(sessionId))
                   .SendAsync(TranscriptionHubEvents.PartialUpdated,
                       new TranscriptPartialUpdatedPush { SessionId = sessionId, Channel = TranscriptionMapper.ToWireChannel(channel), Text = text },
                       cancellationToken);

    public Task PublishStatusAsync(Guid sessionId, LiveEndReason reason, CancellationToken cancellationToken) =>
        _hubContext.Clients
                   .Group(TranscriptionHubGroups.Session(sessionId))
                   .SendAsync(TranscriptionHubEvents.SessionStatusChanged,
                       new TranscriptionSessionStatusPush { SessionId = sessionId, Status = ToWireStatus(reason) },
                       cancellationToken);

    /// <summary>
    ///     The status a client sees for an end reason. <see cref="LiveEndReason.NeverAttached" /> is reported as
    ///     <c>Abandoned</c>: from the browser's side "no audio ever arrived" and "you went away" are the same outcome,
    ///     and the distinction that matters to the engine does not need a fifth case in every client switch.
    /// </summary>
    private static string ToWireStatus(LiveEndReason reason) =>
        reason switch
        {
            LiveEndReason.Completed => "Completed",
            LiveEndReason.Cancelled => "Cancelled",
            LiveEndReason.Abandoned => "Abandoned",
            LiveEndReason.NeverAttached => "Abandoned",
            LiveEndReason.Overloaded => "Overloaded",
            LiveEndReason.Failed => "Failed",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown live transcription end reason.")
        };
}
