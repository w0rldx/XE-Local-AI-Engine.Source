namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Lifecycle status of a transcription session. Persisted as an <see langword="int" /> (the
///     <c>ImageJobStatus</c> precedent), so the ordinals are the schema: append only, never reorder or insert.
/// </summary>
public enum TranscriptionSessionStatus
{
    /// <summary>The session row exists; no audio has been handed to the runtime yet.</summary>
    Created = 0,

    /// <summary>The whisper runtime is actively producing segments for this session.</summary>
    Transcribing = 1,

    /// <summary>Transcription finished and every segment was persisted.</summary>
    Completed = 2,

    /// <summary>Transcription failed; the encrypted error columns carry a display-safe reason.</summary>
    Failed = 3,

    /// <summary>The session was cancelled before it reached a terminal state.</summary>
    Cancelled = 4
}

/// <summary>
///     Where a session's audio comes from. The full set exists from day one because adding a member later would be a
///     schema change; only <see cref="File" /> is reachable in the batch slice. Persisted as an <see langword="int" />:
///     append only.
/// </summary>
public enum TranscriptionSourceKind
{
    /// <summary>An uploaded audio file transcribed in one batch pass.</summary>
    File = 0,

    /// <summary>Live capture from a microphone.</summary>
    Microphone = 1,

    /// <summary>Live capture of the machine's own output (loopback).</summary>
    SystemAudio = 2,

    /// <summary>Live capture of microphone and system audio as two channels of one session.</summary>
    MicrophoneAndSystem = 3,

    /// <summary>Short-form dictation into a composer rather than a standing session.</summary>
    Dictation = 4,

    /// <summary>Live capture scoped to a single application's audio process.</summary>
    ApplicationProcess = 5
}

/// <summary>
///     Which side of a session a segment belongs to. <see cref="Mono" /> is the single-stream case (a file, a
///     microphone-only session); <see cref="You" /> / <see cref="Others" /> separate the two live capture channels.
///     Persisted as an <see langword="int" />: append only.
/// </summary>
public enum TranscriptChannel
{
    /// <summary>One undifferentiated stream.</summary>
    Mono = 0,

    /// <summary>The local speaker's channel (microphone).</summary>
    You = 1,

    /// <summary>The remote side's channel (system audio).</summary>
    Others = 2
}
