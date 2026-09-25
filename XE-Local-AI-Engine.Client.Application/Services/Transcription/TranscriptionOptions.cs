namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.ComponentModel.DataAnnotations;

/// <summary>Configuration for local audio transcription.</summary>
/// <remarks>
///     <see cref="Enabled" /> gates <em>behaviour</em>, never registration — the same posture work sessions and graph
///     workflows hold. The endpoints stay discovered when the feature is off, so the OpenAPI document, and therefore
///     the generated client, is identical on every node; a request-path middleware answers 404 instead.
/// </remarks>
public sealed class TranscriptionOptions
{
    public const string Section = "Transcription";

    /// <summary>Whether the transcription surface answers at all. On by default.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     The appsettings SEED for the idle time-to-live of the whisper daemon, in minutes. The stored node setting
    ///     wins over this; it exists so a first run has a value before anything has been saved.
    /// </summary>
    [Range(1, 240)]
    public int IdleTimeoutMinutes { get; init; } = 15;

    /// <summary>
    ///     How many committed segments one live-session replay may return.
    /// </summary>
    /// <remarks>
    ///     500 is roughly fifteen minutes of two-lane speech. A longer session reconnects truncated and says so, the
    ///     same bargain the graph-workflow run hub strikes with its own event replay limit.
    /// </remarks>
    [Range(1, 10_000)]
    public int SegmentReplayLimit { get; init; } = 500;

    /// <summary>
    ///     How long a live session waits, after its last hub connection drops, before it is cancelled.
    /// </summary>
    /// <remarks>
    ///     Audio still arriving from an in-host capture source does not extend it: a closed tab ends that session
    ///     whatever else is still feeding it.
    /// </remarks>
    [Range(1, 3_600)]
    public int AbandonedSessionGraceSeconds { get; init; } = 60;

    /// <summary>
    ///     The safety cap, in MiB, on one live session's queued-but-untranscribed audio across all its lanes.
    /// </summary>
    /// <remarks>
    ///     A lane that falls behind buffers in memory and catches up; nothing ends a session for being slow. Only this
    ///     cap does, and gracefully (<c>Completed</c>, draining what is queued). 512 MiB is about 4.6 hours of one mono
    ///     lane. Audio is never written to disk.
    /// </remarks>
    [Range(16, 1_048_576)]
    public int MaxBufferedAudioMb { get; init; } = 512;
}
