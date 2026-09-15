namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Where a live session announces what it produced. The application layer knows the events, never the transport:
///     the host supersedes the no-op default with a SignalR-backed implementation.
/// </summary>
/// <remarks>
///     <para>
///         <b>Publishing is not a reaction to a row being written.</b> A commit is a fact the segmenter produced;
///         persisting it is one optional consequence. A persist-free session emits the identical event sequence with
///         nothing in the database behind it, so no implementation may read a published event back out of a store.
///     </para>
///     <para>
///         Enums cross this seam as enums. The wire spelling belongs to the host's publisher, which maps them
///         explicitly, so renaming a member here cannot change the contract a browser sees.
///     </para>
/// </remarks>
public interface ITranscriptionEventPublisher
{
    /// <summary>Announces one durable segment. Called under the session's commit lock, in sequence order.</summary>
    /// <param name="sessionId">The live session.</param>
    /// <param name="seq">The sequence this commit was allocated; ascending from 1 within a session.</param>
    /// <param name="channel">Which lane produced it.</param>
    /// <param name="startMs">Segment start in session audio time.</param>
    /// <param name="endMs">Segment end in session audio time.</param>
    /// <param name="text">The trimmed text.</param>
    /// <param name="confidence">The model's confidence, passed through untouched.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    Task PublishSegmentAsync(Guid sessionId,
        long seq,
        TranscriptChannel channel,
        long startMs,
        long endMs,
        string text,
        double? confidence,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Announces the provisional text of one lane. Sent only when the text actually changed, and never persisted.
    /// </summary>
    Task PublishPartialAsync(Guid sessionId, TranscriptChannel channel, string text, CancellationToken cancellationToken);

    /// <summary>
    ///     Announces that a live session ended, and why. It is the reason rather than a persisted status, so a
    ///     persist-free session — which moves no row at all — still tells its client what happened.
    /// </summary>
    Task PublishStatusAsync(Guid sessionId, LiveEndReason reason, CancellationToken cancellationToken);
}
