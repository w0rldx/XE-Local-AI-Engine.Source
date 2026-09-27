namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The options one transcription session runs under, serialized into the session's encrypted config column.</summary>
public sealed record TranscriptionSessionConfig
{
    /// <summary><c>auto</c> to let the model detect the language, <c>override</c> to force one.</summary>
    public required string LanguageMode { get; init; }

    /// <summary>The ISO-639-1 code to force; required when <see cref="LanguageMode" /> is <c>override</c>.</summary>
    public string? LanguageOverride { get; init; }

    /// <summary>Translate the transcript to English. English is the only target the model has.</summary>
    public bool Translate { get; init; }

    /// <summary>
    ///     The live-capture window in seconds. The batch path ignores it and stores it unchanged, so a session created
    ///     from a file and later resumed live keeps one set of options.
    /// </summary>
    public int MaxWindowSeconds { get; init; } = 5;

    /// <summary>Whether channels are attributed separately. A file is a single channel, so this is false here.</summary>
    public bool ChannelAttribution { get; init; }
}

/// <summary>The parameters for a new transcription session as the endpoint hands them over.</summary>
public sealed record CreateTranscriptionSessionInput
{
    /// <summary>
    ///     The session title. Blank falls back to a generated one; the file-upload flow sends the chosen file's name.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>The source kind's name (<c>File</c>, <c>Microphone</c>, …). Blank means <c>File</c>.</summary>
    public string? SourceKind { get; init; }

    /// <summary>The transcription model to use; blank resolves to the node's effective model.</summary>
    public string? ModelId { get; init; }

    /// <summary>The session options; <see langword="null" /> stores the defaults.</summary>
    public TranscriptionSessionConfig? Config { get; init; }
}

/// <summary>One page of transcription sessions.</summary>
public sealed class TranscriptionSessionPage
{
    /// <summary>The sessions on this page, newest first.</summary>
    public required IReadOnlyList<TranscriptionSessionSummaryView> Items { get; init; }

    /// <summary>How many sessions exist in total, ignoring paging.</summary>
    public required int TotalCount { get; init; }
}

/// <summary>Which of the five ways a batch transcription can end actually happened.</summary>
public enum TranscribeFileOutcome
{
    /// <summary>The transcript was produced and persisted.</summary>
    Succeeded = 0,

    /// <summary>The session id is unknown, or the session had already reached a terminal state.</summary>
    SessionNotFound = 1,

    /// <summary>The uploaded bytes are not a container this node can transcribe.</summary>
    UnsupportedContainer = 2,

    /// <summary>The caller cancelled, or the session was cancelled while it ran.</summary>
    Cancelled = 3,

    /// <summary>The runtime or the transcode step failed. The session records the sanitized reason.</summary>
    RuntimeFailed = 4,

    /// <summary>The upload holds no audio (an empty file or a header-only WAV); the row is left untouched.</summary>
    NoAudio = 5,

    /// <summary>The session had already reached a terminal state; the row is left untouched.</summary>
    SessionFinished = 6
}

/// <summary>
///     The outcome of one batch transcription, as data rather than as an exception.
/// </summary>
/// <remarks>
///     An unsupported container and a cancellation are expected answers, not faults: modelling them as a result lets
///     the endpoint map every outcome in one switch instead of catching exceptions for things that are not errors.
/// </remarks>
public sealed record TranscribeFileResult
{
    /// <summary>What happened.</summary>
    public required TranscribeFileOutcome Outcome { get; init; }

    /// <summary>The session; set when <see cref="Outcome" /> is succeeded (with its transcript) or session-finished.</summary>
    public TranscriptionSessionDetailView? Session { get; init; }

    /// <summary>What the bytes actually were; set only for an unsupported container.</summary>
    public AudioContainer DetectedContainer { get; init; }

    /// <summary>The extensions this node accepts right now; set only for an unsupported container.</summary>
    public IReadOnlyList<string> SupportedContainers { get; init; } = [];

    /// <summary>
    ///     True when the container would be accepted if <c>ffmpeg</c> were installed. This is what turns a refusal into
    ///     an actionable one.
    /// </summary>
    public bool FfmpegRequired { get; init; }

    /// <summary>A stable machine-readable reason; set for an unsupported container and for a runtime failure.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>A sanitized, display-safe explanation; set for an unsupported container and for a runtime failure.</summary>
    public string? ErrorMessage { get; init; }

    internal static TranscribeFileResult Succeeded(TranscriptionSessionDetailView session) =>
        new()
        {
            Outcome = TranscribeFileOutcome.Succeeded,
            Session = session
        };

    internal static TranscribeFileResult SessionNotFound() =>
        new()
        {
            Outcome = TranscribeFileOutcome.SessionNotFound
        };

    internal static TranscribeFileResult NoAudio(string message) =>
        new()
        {
            Outcome = TranscribeFileOutcome.NoAudio,
            ErrorCode = "no-audio",
            ErrorMessage = message
        };

    internal static TranscribeFileResult SessionFinished(TranscriptionSessionDetailView session) =>
        new()
        {
            Outcome = TranscribeFileOutcome.SessionFinished,
            Session = session
        };

    internal static TranscribeFileResult Cancelled() =>
        new()
        {
            Outcome = TranscribeFileOutcome.Cancelled
        };

    internal static TranscribeFileResult RuntimeFailed(string code, string message) =>
        new()
        {
            Outcome = TranscribeFileOutcome.RuntimeFailed,
            ErrorCode = code,
            ErrorMessage = message
        };

    internal static TranscribeFileResult UnsupportedContainer(AudioContainer detected,
        IReadOnlyList<string> supported,
        bool ffmpegRequired,
        string message) =>
        new()
        {
            Outcome = TranscribeFileOutcome.UnsupportedContainer,
            DetectedContainer = detected,
            SupportedContainers = supported,
            FfmpegRequired = ffmpegRequired,
            ErrorCode = "unsupported-container",
            ErrorMessage = message
        };
}

/// <summary>Raised when engine-side transcoding could not produce a WAV. The message is sanitized for display.</summary>
public sealed class AudioTranscodeException : Exception
{
    /// <summary>Creates the exception with a sanitized, display-safe message.</summary>
    public AudioTranscodeException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a sanitized, display-safe message and the underlying cause.</summary>
    public AudioTranscodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Which of the four ways a live-session start can end actually happened.</summary>
public enum StartLiveOutcome
{
    /// <summary>The lanes were registered and the row moved to <c>Transcribing</c>.</summary>
    Started = 0,

    /// <summary>The session was already live; nothing was registered twice.</summary>
    AlreadyLive = 1,

    /// <summary>The session id is unknown.</summary>
    SessionNotFound = 2,

    /// <summary>The session had already reached a terminal state and cannot be started again.</summary>
    SessionAlreadyFinished = 3
}

/// <summary>
///     The outcome of one live-session start, as data rather than as an exception.
/// </summary>
/// <remarks>
///     An unknown session and a finished one are expected answers a caller maps to a status code, not faults. A
///     source kind that cannot be captured live is different: it is a malformed request, and it throws.
/// </remarks>
public sealed record StartLiveResult
{
    /// <summary>What happened.</summary>
    public required StartLiveOutcome Outcome { get; init; }

    /// <summary>The session's status after the call; meaningful for <c>Started</c> and <c>AlreadyLive</c>.</summary>
    public TranscriptionSessionStatus Status { get; init; }

    /// <summary>
    ///     The highest sequence the session has persisted, so a client subscribing after the start knows what to ask
    ///     the hub to replay from.
    /// </summary>
    public long LastSeq { get; init; }

    /// <summary>The options the session was registered with; set only when it was started by this call.</summary>
    public LiveSessionOptions? Options { get; init; }
}

/// <summary>
///     Raised when a live session is asked of a source kind that has no live capture path — an uploaded file.
/// </summary>
/// <remarks>
///     A fault rather than an outcome: every other refusal describes a session that exists and is in the wrong state,
///     while this one describes a request that could never have succeeded for this session at any time.
/// </remarks>
public sealed class LiveTranscriptionSourceKindException : Exception
{
    /// <summary>Creates the exception for a source kind that cannot be captured live.</summary>
    public LiveTranscriptionSourceKindException(TranscriptionSourceKind sourceKind)
        : base($"A {sourceKind} transcription session has no live capture path.") =>
        SourceKind = sourceKind;

    /// <summary>Creates the exception with an explicit message.</summary>
    public LiveTranscriptionSourceKindException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an explicit message and the underlying cause.</summary>
    public LiveTranscriptionSourceKindException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The source kind that was refused.</summary>
    public TranscriptionSourceKind SourceKind { get; }
}

/// <summary>Which of the four ways a transcript-row edit can end actually happened.</summary>
public enum UpdateTranscriptSegmentOutcome
{
    /// <summary>The row holds the new text.</summary>
    Updated = 0,

    /// <summary>The session id is unknown.</summary>
    SessionNotFound = 1,

    /// <summary>The session holds no row with that sequence.</summary>
    SegmentNotFound = 2,

    /// <summary>The session is still transcribing; its transcript is not the operator's to edit yet.</summary>
    SessionTranscribing = 3
}

/// <summary>The outcome of one transcript-row edit, as data rather than as an exception.</summary>
public sealed record UpdateTranscriptSegmentResult
{
    /// <summary>What happened.</summary>
    public required UpdateTranscriptSegmentOutcome Outcome { get; init; }

    /// <summary>The row as it now reads; set only when <see cref="Outcome" /> is <c>Updated</c>.</summary>
    public TranscriptSegmentView? Segment { get; init; }
}
