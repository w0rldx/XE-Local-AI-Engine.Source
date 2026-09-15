namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for transcription sessions (<c>transcription_sessions</c>) and their transcript rows
///     (<c>transcript_segments</c>). The title, config, error pair and every segment's text are encrypted at rest by the
///     node encryption interceptors, so every write goes through EF
///     <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)" /> and
///     never raw SQL — a status-only update leaves the encrypted properties unmodified, so the interceptor skips them and
///     the stored ciphertext survives. Reads decrypt through the materialization interceptor. Scoped: one
///     <see cref="NodeChatDbContext" /> per operation.
///     <para>
///         No member of this contract carries audio. A session records what was said, never what was heard: the uploaded
///         bytes live in a temp file the caller owns for the duration of one transcription and are deleted with it.
///     </para>
/// </summary>
public interface ITranscriptionSessionStore
{
    /// <summary>Inserts a new session in the <see cref="TranscriptionSessionStatus.Created" /> state.</summary>
    Task CreateAsync(TranscriptionSessionCreate create, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads one session with its transcript rows ordered by <see cref="TranscriptSegmentView.Seq" />, decrypted, or
    ///     <see langword="null" /> when the session does not exist.
    /// </summary>
    Task<TranscriptionSessionDetailView?> GetWithSegmentsAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     One page of sessions, newest first, without their transcript rows. Ordered by
    ///     <see cref="TranscriptionSessionSummaryView.CreatedAtUtc" /> descending and then by id descending, so two
    ///     sessions created in the same millisecond keep a stable order across pages.
    ///     <see cref="TranscriptionSessionSummaryView.SegmentCount" /> is counted in the database, so the list shows how
    ///     long a transcript is without decrypting a single row of it.
    /// </summary>
    Task<IReadOnlyList<TranscriptionSessionSummaryView>> ListAsync(int limit, int offset, CancellationToken cancellationToken);

    /// <summary>The total number of sessions, ignoring paging.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Deletes a session and, by cascade, its transcript rows. False when the session did not exist.</summary>
    Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>Moves a session to <paramref name="status" />. False when the session did not exist.</summary>
    Task<bool> SetStatusAsync(Guid sessionId, TranscriptionSessionStatus status, long updatedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    ///     Moves a session from <paramref name="expected" /> to <paramref name="desired" />, and only from there.
    ///     False — writing nothing — when the session does not exist or is no longer in <paramref name="expected" />.
    /// </summary>
    /// <remarks>
    ///     The compare and the write are one <c>SaveChangesAsync</c> on a tracked row, so a caller acting on a status
    ///     it read earlier cannot overwrite a terminal state another writer reached in between. A live start and a
    ///     graceful end race for exactly this row, and the loser must not resurrect it.
    /// </remarks>
    Task<bool> TryTransitionStatusAsync(Guid sessionId,
        TranscriptionSessionStatus expected,
        TranscriptionSessionStatus desired,
        long updatedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    ///     One session without its transcript, decrypted, or <see langword="null" /> when the id is unknown.
    ///     <see cref="TranscriptionSessionSummaryView.SegmentCount" /> is counted in the database.
    /// </summary>
    /// <remarks>
    ///     The read for a caller that wants the session's state rather than its text.
    ///     <see cref="GetWithSegmentsAsync" /> includes and decrypts every row, which for a long live session is the
    ///     whole transcript loaded to answer a question about one column.
    /// </remarks>
    Task<TranscriptionSessionSummaryView?> GetSummaryAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     The highest <see cref="TranscriptSegmentView.Seq" /> the session holds, or 0 when it holds none.
    /// </summary>
    /// <remarks>
    ///     Not the segment count: a transcript with a gap in it — a dropped commit, a partial restore — would make a
    ///     count re-allocate a sequence the unique <c>(session_id, seq)</c> index already holds.
    /// </remarks>
    Task<long> GetLastSeqAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a session <see cref="TranscriptionSessionStatus.Completed" />, recording the detected language and the
    ///     audio duration. False when the session did not exist.
    /// </summary>
    Task<bool> CompleteAsync(Guid sessionId, string? detectedLanguage, long durationMs, long updatedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks a session <see cref="TranscriptionSessionStatus.Failed" /> with an encrypted code/message pair. The code
    ///     is encrypted with the message on purpose (R11), so it is deliberately not SQL-queryable. False when the
    ///     session did not exist.
    /// </summary>
    Task<bool> FailAsync(Guid sessionId, string errorCode, string errorMessage, long updatedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    ///     Appends transcript rows and bumps the session's <c>updated_at_utc</c> in one <c>SaveChangesAsync</c>.
    ///     <see cref="TranscriptSegmentWrite.Seq" /> is allocated by the caller; the unique <c>(session_id, seq)</c>
    ///     index is what stops two writers double-allocating, so a repeated sequence throws rather than silently
    ///     interleaving.
    ///     <para>
    ///         False when the session does not exist, and nothing is written. The existence check is not belt-and-braces:
    ///         the node connection leaves <c>PRAGMA foreign_keys</c> off, so an unknown session id would otherwise insert
    ///         orphan rows no read path can ever reach.
    ///     </para>
    /// </summary>
    Task<bool> AppendSegmentsAsync(Guid sessionId, IReadOnlyList<TranscriptSegmentWrite> segments, long updatedAtUtc, CancellationToken cancellationToken);

    /// <summary>
    ///     One page of transcript rows after a watermark, ordered by <see cref="TranscriptSegmentView.Seq" /> and
    ///     decrypted. <paramref name="afterSeq" /> is an EXCLUSIVE lower bound, so a subscriber resuming from the last
    ///     sequence it saw does not receive that row a second time; an unknown session returns an empty page rather
    ///     than throwing.
    ///     <para>
    ///         Unlike <see cref="GetWithSegmentsAsync" /> this bounds the read in SQL. A live session's replay has to
    ///         answer "what did I miss" without decrypting a transcript that may already run to thousands of rows.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<TranscriptSegmentView>> ListSegmentsAfterAsync(Guid sessionId, long afterSeq, int limit, CancellationToken cancellationToken);
}

/// <summary>
///     The parameters for a new transcription session. <see cref="Title" /> and <see cref="ConfigJson" /> are plaintext
///     here; the store encodes them to UTF-8 bytes and the node encryption interceptor encrypts them at rest.
/// </summary>
public sealed record TranscriptionSessionCreate
{
    public required Guid Id { get; init; }
    public string? Title { get; init; }
    public required TranscriptionSourceKind SourceKind { get; init; }
    public required string ModelId { get; init; }

    /// <summary>The session's resolved options as JSON. Required — a session with no options stores <c>{}</c>.</summary>
    public required string ConfigJson { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     One transcript row to append. <see cref="Seq" /> is the caller's allocation (ascending, starting at 1);
///     <see cref="Text" /> is plaintext here and encrypted at rest by the interceptor.
/// </summary>
public sealed record TranscriptSegmentWrite
{
    public required long Seq { get; init; }
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    public required string Text { get; init; }
    public required TranscriptChannel Channel { get; init; }
    public double? Confidence { get; init; }
}

/// <summary>A decrypted transcript row.</summary>
public sealed record TranscriptSegmentView
{
    public required Guid Id { get; init; }
    public required long Seq { get; init; }
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    public required string Text { get; init; }
    public required TranscriptChannel Channel { get; init; }
    public double? Confidence { get; init; }
}

/// <summary>
///     A decrypted, transport-neutral view of a session without its transcript rows — what the session list renders.
///     It deliberately carries no error pair: a list of sessions has no use for one, and leaving it off keeps the
///     failure detail behind an explicit read of one session.
///     <para>
///         <see cref="ConfigJson" /> rides along because it is free: every read that produces this view materializes
///         the whole entity, so the interceptor has already decrypted that column. The list DTO still does not
///         expose it — the live-start path is what needs it, and it needs it without loading a transcript.
///     </para>
/// </summary>
public sealed record TranscriptionSessionSummaryView
{
    public required Guid Id { get; init; }
    public string? Title { get; init; }
    public required long CreatedAtUtc { get; init; }
    public required long UpdatedAtUtc { get; init; }
    public required TranscriptionSessionStatus Status { get; init; }
    public required TranscriptionSourceKind SourceKind { get; init; }
    public required string ModelId { get; init; }

    /// <summary>The session's resolved options as JSON, exactly as <c>TranscriptionSessionDetailView</c> carries it.</summary>
    public required string ConfigJson { get; init; }

    public string? DetectedLanguage { get; init; }
    public long? DurationMs { get; init; }

    /// <summary>How many transcript rows the session holds. Counted in SQL; no segment is loaded or decrypted.</summary>
    public required int SegmentCount { get; init; }
}

/// <summary>A decrypted view of one session together with its transcript rows, ordered by sequence.</summary>
public sealed record TranscriptionSessionDetailView
{
    public required Guid Id { get; init; }
    public string? Title { get; init; }
    public required long CreatedAtUtc { get; init; }
    public required long UpdatedAtUtc { get; init; }
    public required TranscriptionSessionStatus Status { get; init; }
    public required TranscriptionSourceKind SourceKind { get; init; }
    public required string ModelId { get; init; }
    public required string ConfigJson { get; init; }
    public string? DetectedLanguage { get; init; }
    public long? DurationMs { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>How many transcript rows the session holds — here simply the length of <see cref="Segments" />.</summary>
    public required int SegmentCount { get; init; }

    public required IReadOnlyList<TranscriptSegmentView> Segments { get; init; }
}
