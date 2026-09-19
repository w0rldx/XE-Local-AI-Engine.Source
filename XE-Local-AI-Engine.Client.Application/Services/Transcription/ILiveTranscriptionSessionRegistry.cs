namespace XE_Local_AI_Engine.Client.Services.Transcription;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Owns every live transcription session on this node: the lanes, their commit pipeline and the one path a
///     session ends through.
/// </summary>
/// <remarks>
///     <para>
///         <b>Audio enters here, not through <see cref="ITranscriptionService" />.</b> <see cref="PushAudioAsync" />
///         knows nothing about SignalR, connections or hub contexts, so the browser (through the hub) and an in-host
///         capture source are two callers of one method rather than two paths. A persist-free session never touches
///         <see cref="ITranscriptionService" /> at all, which is why the seam cannot live there.
///     </para>
///     <para>
///         Registered as a singleton with no <c>IHostedService</c>: the end-to-end test factory removes every hosted
///         service, so a background-timer design would be dead there. Everything is driven by pushed frames plus
///         timers created from the injected <see cref="TimeProvider" />.
///     </para>
/// </remarks>
public interface ILiveTranscriptionSessionRegistry
{
    /// <summary>
    ///     Registers a live session and its lanes. Takes an options object rather than a session id so a persist-free
    ///     session, which has no row to look up, uses the same entry point.
    /// </summary>
    /// <exception cref="LiveSessionAlreadyRegisteredException">The session is already live.</exception>
    Task StartLiveSessionAsync(Guid sessionId, LiveSessionOptions options, CancellationToken cancellationToken);

    /// <summary>
    ///     Hands one frame of 16 kHz mono little-endian int16 PCM to a lane. It returns as soon as the frame is
    ///     queued: the caller is a capture callback and must never wait behind an inference.
    /// </summary>
    /// <remarks>
    ///     A frame for a session that is not live, or one that arrives after the session started ending, is dropped
    ///     here rather than throwing — the hub refuses those before they reach this method.
    /// </remarks>
    /// <exception cref="ArgumentException">The session has no lane for <paramref name="channel" />.</exception>
    Task PushAudioAsync(Guid sessionId, TranscriptChannel channel, ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken);

    /// <summary>
    ///     The only way a live session ends. Idempotent: a second call returns the first call's task, and it never
    ///     throws to its caller.
    /// </summary>
    Task EndAsync(Guid sessionId, LiveEndReason reason, CancellationToken cancellationToken);

    /// <summary>
    ///     Registers the thing producing audio for this session and returns the token it must stop on. Called by an
    ///     in-host producer (native process capture); the browser is not a producer in this sense — the hub is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is not live.</exception>
    LiveProducerRegistration AttachProducer(Guid sessionId, ILiveAudioProducer producer);

    /// <summary>Records a hub connection as watching this session, and disarms any abandonment grace.</summary>
    void NoteBrowserAttached(Guid sessionId, string connectionId);

    /// <summary>
    ///     Removes a hub connection. The set going empty arms the abandonment grace; audio still arriving from an
    ///     in-host producer deliberately does not cancel it, because a closed tab ends that session.
    /// </summary>
    void NoteBrowserDetached(Guid sessionId, string connectionId);

    /// <summary>Whether this session has lanes that accept audio right now.</summary>
    bool IsLive(Guid sessionId);
}

/// <summary>Why a live session ended. Maps to the persisted status and to the session-status push.</summary>
public enum LiveEndReason
{
    /// <summary>The operator ended the session; the lanes flushed and the transcript is whole.</summary>
    Completed = 0,

    /// <summary>The session was cancelled or deleted while it was live.</summary>
    Cancelled = 1,

    /// <summary>Every hub connection dropped and none came back inside the grace.</summary>
    Abandoned = 2,

    /// <summary>No audio ever arrived before the producer-attachment deadline — a denied microphone, typically.</summary>
    NeverAttached = 3,

    /// <summary>Audio arrived faster than the lanes consumed it and the pending budget overflowed.</summary>
    Overloaded = 4,

    /// <summary>A lane stopped making progress, or the runtime failed.</summary>
    Failed = 5
}

/// <summary>
///     Raised when a session is registered that is already live.
/// </summary>
/// <remarks>
///     A dedicated type, not a bare <see cref="InvalidOperationException" />, because the caller's response is
///     specific and must not be reached by anything else: the loser of a registration race reports the winner's
///     state and touches no row. Matching on the base type caught <see cref="ObjectDisposedException" /> too — a
///     registry shutting down looked like a duplicate start — and pairing that match with a liveness check made the
///     answer depend on whether the winner had begun ending, which is exactly the state the loser must not consult.
/// </remarks>
public sealed class LiveSessionAlreadyRegisteredException : Exception
{
    /// <summary>Creates the exception for the session that was already live.</summary>
    public LiveSessionAlreadyRegisteredException(Guid sessionId)
        : base($"Transcription session {sessionId} is already live.") =>
        SessionId = sessionId;

    /// <summary>Creates the exception with an explicit message.</summary>
    public LiveSessionAlreadyRegisteredException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an explicit message and the underlying cause.</summary>
    public LiveSessionAlreadyRegisteredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The session that was already registered.</summary>
    public Guid SessionId { get; }
}

/// <summary>
///     An in-host source of audio for one session. <see cref="StopAsync" /> must stop capture and return; it must not
///     wait on inference, and it is called at most once per session.
/// </summary>
public interface ILiveAudioProducer
{
    /// <summary>Stops capture. Bounded by the registry: a producer that hangs is abandoned, never awaited forever.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>What a producer gets back: the token it stops on, and the handle that detaches it.</summary>
[SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
    Justification =
        "A record's positional parameters are its properties, read by name; the token is what a producer reaches for first, and the trailing-parameter convention is about call sites that pass one through.")]
public sealed class LiveProducerRegistration
{
    /// <summary>Cancelled as the first act of ending the session, before anything is flushed.</summary>
    public required CancellationToken ProducerToken { get; init; }

    /// <summary>Disposed by a producer that stops on its own, so the session stops calling it.</summary>
    public required IDisposable Detach { get; init; }
}

/// <summary>Everything one live session runs under. One shape for the persisted and the persist-free case alike.</summary>
public sealed record LiveSessionOptions
{
    /// <summary>The whisper model every submission names.</summary>
    public required string ModelId { get; init; }

    /// <summary>A forced language code, or <see langword="null" /> to let the model detect one.</summary>
    public string? Language { get; init; }

    /// <summary>Whether the model translates to English.</summary>
    public bool Translate { get; init; }

    /// <summary>The window, guard and tick sizes every lane of this session runs with.</summary>
    public required LiveSegmenterSettings Settings { get; init; }

    /// <summary>The lanes to create. One entry per channel this session carries.</summary>
    public required IReadOnlyList<TranscriptChannel> Channels { get; init; }

    /// <summary>
    ///     The highest sequence number already persisted for this session. The first live commit is numbered one past
    ///     it, so a session resumed after a batch pass does not re-allocate a taken sequence.
    /// </summary>
    public long StartingSeq { get; init; }

    /// <summary>Which entity row the session represents. <c>Dictation</c> is the persist-free case.</summary>
    public required TranscriptionSourceKind SourceKind { get; init; }

    /// <summary>
    ///     Whether committed segments are written to the transcript. Default <see langword="true" />.
    ///     <see langword="false" /> is the dictation mode: the commit event still fires and still reaches the hub, but
    ///     no row is ever written and no session row is expected to exist.
    /// </summary>
    public bool Persist { get; init; } = true;
}
