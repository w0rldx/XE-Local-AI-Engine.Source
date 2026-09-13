namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The application-layer facade the transcription session endpoints call: session lifecycle plus the batch
///     file-transcription path.
/// </summary>
/// <remarks>
///     <para>
///         Registered as a singleton. It owns the in-flight cancellation registry, which must outlive the request that
///         started a transcription, and it composes the singleton whisper runtime; it opens its own dependency-injection
///         scope per store operation, the same posture the image job coordinator uses.
///     </para>
///     <para>
///         No member of this contract carries audio. The uploaded bytes live in a temporary file the caller owns for
///         exactly one transcription and are deleted with the <see cref="TranscriptionUploadSlot" /> that minted it.
///     </para>
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

    /// <summary>One page of sessions, newest first, with the unpaged total.</summary>
    Task<TranscriptionSessionPage> ListSessionsAsync(int limit, int offset, CancellationToken cancellationToken);

    /// <summary>Cancels any in-flight transcription, then deletes the session and its transcript.</summary>
    Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken);

    /// <summary>
    ///     Signals the in-flight transcription for this session. False when nothing is running for it.
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
}
