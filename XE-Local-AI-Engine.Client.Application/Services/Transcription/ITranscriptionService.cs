namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The application-layer facade the transcription session endpoints call: session lifecycle plus the batch
///     file-transcription path.
/// </summary>
/// <remarks>
///     Registered as a singleton: it owns the in-flight cancellation registry, which must outlive the request that started a
///     transcription, and it composes the singleton whisper runtime, opening its own dependency-injection scope per store
///     operation — the same posture the image job coordinator uses. No member of this contract carries audio: the uploaded
///     bytes live in a temporary file the caller owns for exactly one transcription and are deleted with the
///     <see cref="TranscriptionUploadSlot" /> that minted it.
/// </remarks>
public interface ITranscriptionService
{
    /// <summary>
    ///     Creates a session in the <c>Created</c> state, resolving the effective model when the input names none.
    /// </summary>
    /// <exception cref="ArgumentException">The source kind is not a known one.</exception>
    Task<TranscriptionSessionDetailView> CreateSessionAsync(CreateTranscriptionSessionInput input, CancellationToken cancellationToken);

    /// <summary>Reads one session with its transcript, or <see langword="null" /> when the id is unknown.</summary>
    Task<TranscriptionSessionDetailView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     One session's state without its transcript, or <see langword="null" /> when the id is unknown.
    /// </summary>
    /// <remarks>
    ///     The read for a caller that needs the status and the segment count rather than the text.
    ///     <see cref="GetSessionAsync" /> decrypts every row, which for a long live session is the whole transcript
    ///     loaded to answer a question about one column.
    /// </remarks>
    Task<TranscriptionSessionSummaryView?> GetSessionSummaryAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>One page of sessions, newest first, with the unpaged total.</summary>
    Task<TranscriptionSessionPage> ListSessionsAsync(int limit, int offset, CancellationToken cancellationToken);

    /// <summary>Cancels any in-flight transcription, then deletes the session and its transcript.</summary>
    Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     Cancels an in-flight transcription, interrupting and joining a live finalization or signalling a batch job.
    ///     False when neither a running job nor a live finalization is registered.
    /// </summary>
    Task<bool> CancelAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     Mints the engine-owned temporary file the endpoint streams the upload into. <b>The caller owns the returned
    ///     slot and must dispose it</b>; disposal is the only thing that deletes the audio.
    /// </summary>
    Task<TranscriptionUploadSlot> BeginUploadAsync(Guid sessionId, string extension, CancellationToken cancellationToken);

    /// <summary>
    ///     Transcribes the already-filled owned file. Deletes nothing: the slot owns every path it handed out.
    /// </summary>
    Task<TranscribeFileResult> TranscribeFileAsync(TranscriptionUploadSlot slot, CancellationToken cancellationToken);

    /// <summary>
    ///     Starts live capture for an existing session: reads the row, builds its <see cref="LiveSessionOptions" />,
    ///     registers the lanes and moves the row to <c>Transcribing</c>.
    /// </summary>
    /// <remarks>
    ///     It reads ONLY the row: model from the row, language, translation and window from its stored
    ///     config, channels from its source kind. Nothing consults a node setting, so a session transcribes under the
    ///     options it was created with however long it waited. Idempotent and rolling back — an already-live session
    ///     returns its state without registering a second set of lanes, and a registration that throws leaves the row
    ///     in <c>Created</c>, never stranded in <c>Transcribing</c>. A persist-free session has no row and calls <see cref="ILiveTranscriptionSessionRegistry.StartLiveSessionAsync" /> instead.
    /// </remarks>
    /// <exception cref="LiveTranscriptionSourceKindException">The session's source kind has no live capture path.</exception>
    Task<StartLiveResult> StartLiveAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     Persists one committed live segment under the sequence the registry's commit pipeline allocated for it.
    /// </summary>
    Task AppendLiveSegmentAsync(Guid sessionId,
        long seq,
        TranscriptChannel channel,
        long startMs,
        long endMs,
        string text,
        double? confidence,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Moves a live session's row to its terminal state. Called only for a session that persists at all; the
    ///     registry maps the end reason to the status, because the reason is what maps to a status and not the
    ///     other way round.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="finalStatus">The terminal status the end reason resolved to.</param>
    /// <param name="durationMs">How much audio the session received.</param>
    /// <param name="detectedLanguage">The language a lane reported, if any.</param>
    /// <param name="errorCode">A stable machine-readable reason; set only for a failed end.</param>
    /// <param name="errorMessage">A sanitized, display-safe explanation; set only for a failed end.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task CompleteLiveAsync(Guid sessionId,
        TranscriptionSessionStatus finalStatus,
        long durationMs,
        string? detectedLanguage,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken);

    /// <summary>
    ///     One page of transcript rows after a watermark, ordered by sequence — the live hub's replay read.
    /// </summary>
    /// <remarks>
    ///     Sequence order, not start time. With two lanes a sequence is commit order and a start time is speech
    ///     order; the session view sorts by the latter and a resuming subscriber needs the former.
    /// </remarks>
    Task<IReadOnlyList<TranscriptSegmentView>> ListSegmentsAfterAsync(Guid sessionId, long afterSeq, int limit, CancellationToken cancellationToken);
}
