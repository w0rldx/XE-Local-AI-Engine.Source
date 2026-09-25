namespace XE_Local_AI_Engine.Client.Services.Transcription.Live;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Default <see cref="ILiveTranscriptionSessionRegistry" />: one segmenter per channel, one commit pipeline per
///     session, one way out.
/// </summary>
/// <remarks>
///     A lane is a queue, not a call: <see cref="PushAudioAsync" /> appends the frame to that lane's task chain and
///     returns, because a capture callback that waited for the transcriber would drop audio at the source, the one
///     place the engine can never get it back from. Sequence allocation, persistence and publication are ONE critical
///     section, because the client's watermark depends on publication order. Persistence is a subscriber of a commit,
///     never its source. See docs/wiki/24-audio-transcription.md ("The registry").
/// </remarks>
public sealed class LiveTranscriptionSessionRegistry : ILiveTranscriptionSessionRegistry, IAsyncDisposable
{
    // The clock starts at REGISTRATION, not at the first frame: a session whose microphone permission was denied
    // registers lanes nothing ever feeds, and no browser ever disconnects to arm the abandonment grace.
    private static readonly TimeSpan ProducerAttachmentTimeout = TimeSpan.FromSeconds(60);

    // A producer that hangs must not hang the session; after this it is abandoned and the failure logged.
    private static readonly TimeSpan ProducerStopTimeout = TimeSpan.FromSeconds(5);

    // What a graceful end records when it could not finalize its last window. The message is display-safe: it says
    // what the operator lost, because the retained audio is gone by the time they read it.
    private const string FlushFailedErrorCode = "live-flush-failed";

    private const string FlushFailedErrorMessage =
        "The final window could not be transcribed; the transcript may be missing its last seconds.";

    // Abort cleanup stays short; a graceful end has no deadline (per request the inference timeout, per lane the stall
    // detector, and Cancel interrupts it). Shutdown has its own bound; persistence inside the commit gate is never abandoned.
    private static readonly TimeSpan LaneDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(3);

    // Catch-up progress is reported at most once per this much audio consumed while behind.
    private const long CatchUpReportBytes = 1_000L * WavPcm16.BytesPerMillisecond;

    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();
    private readonly IWhisperTranscriber _transcriber;
    private readonly ITranscriptionEventPublisher _publisher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TranscriptionOptions _options;
    private readonly ILogger<LiveTranscriptionSessionRegistry> _logger;
    private readonly long _maxBufferedBytes;
    private bool _disposed;

    /// <summary>Creates the registry.</summary>
    /// <param name="transcriber">The runtime every lane submits its windows to.</param>
    /// <param name="publisher">Where commits, partials and the final status go.</param>
    /// <param name="scopeFactory">
    ///     Resolved at call time: injecting <see cref="ITranscriptionService" /> would close a constructor cycle,
    ///     since it resolves this registry for the one termination path.
    /// </param>
    /// <param name="options">Carries the abandonment grace and the buffered-audio safety cap.</param>
    /// <param name="timeProvider">Every timer in this class comes from here; there is no hosted service.</param>
    /// <param name="logger">Ending a session never throws to its caller, so failures are only visible here.</param>
    public LiveTranscriptionSessionRegistry(IWhisperTranscriber transcriber,
        ITranscriptionEventPublisher publisher,
        IServiceScopeFactory scopeFactory,
        IOptions<TranscriptionOptions> options,
        TimeProvider timeProvider,
        ILogger<LiveTranscriptionSessionRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options.Value;
        _maxBufferedBytes = (long)_options.MaxBufferedAudioMb * 1024 * 1024;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "A live session's cancellation sources outlive this call and are deliberately left to the collector, so a frame still holding a reference cannot observe a disposed one.")]
    public Task StartLiveSessionAsync(Guid sessionId, LiveSessionOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (options.Channels.Count == 0)
        {
            throw new ArgumentException("A live session needs at least one channel.", nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // The session-wide source is built first so each lane can link to it: cancelling the session still cancels
        // every lane, while a single lane that will not drain can be cancelled on its own.
        var abort = new CancellationTokenSource();
        var lanes = options.Channels
                           .Distinct()
                           .ToDictionary(channel => channel,
                               channel => new Lane(channel,
                                   new LiveTranscriptionSegmenter(_transcriber,
                                       channel,
                                       options.ModelId,
                                       options.Language,
                                       options.Translate,
                                       options.Settings,
                                       _timeProvider),
                                   CancellationTokenSource.CreateLinkedTokenSource(abort.Token)));

        var session = new LiveSession
        {
            Id = sessionId,
            Abort = abort,
            Options = options,
            Lanes = lanes,
            Seq = options.StartingSeq,
            StartedAt = _timeProvider.GetUtcNow()
        };

        if (!_sessions.TryAdd(sessionId, session))
        {
            throw new LiveSessionAlreadyRegisteredException(sessionId);
        }

        lock (session.Gate)
        {
            session.AttachmentTimer = _timeProvider.CreateTimer(EndOnTimer,
                new TimerState
                {
                    Registry = this,
                    Session = session,
                    Reason = LiveEndReason.NeverAttached
                },
                ProducerAttachmentTimeout,
                Timeout.InfiniteTimeSpan);
        }

        return Task.CompletedTask;
    }

    public Task PushAudioAsync(Guid sessionId, TranscriptChannel channel, ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return Task.CompletedTask;
        }

        if (!session.Lanes.TryGetValue(channel, out var lane))
        {
            throw new ArgumentException($"Live transcription session {sessionId} carries no {channel} channel.", nameof(channel));
        }

        if (pcm16.IsEmpty)
        {
            return Task.CompletedTask;
        }

        // Copied before it is queued and never referenced again: this method returns while the frame is still queued, so an in-process capture source may reuse its
        // buffer at once, and holding the caller's memory would put replacement audio in the transcript. The hub path is safe already (SignalR hands a fresh array per invocation); this seam is not.
        var owned = pcm16.ToArray();
        var capReached = false;

        // Admission, the cap check and the queue insertion are ONE critical section, taken on the same gate BeginEnd closes admission under. Splitting them lets a push pass
        // admission, pause, and append to a lane whose chain termination had snapshotted — a second concurrent call into a single-threaded segmenter, after the session was declared over.
        lock (session.Gate)
        {
            if (session.AdmissionClosed)
            {
                return Task.CompletedTask;
            }

            // Audio from ANY producer disarms the attachment deadline; it deliberately does not touch the browser
            // abandonment grace, because a closed tab ends the session whatever else is still feeding it.
            session.AttachmentTimer?.Dispose();
            session.AttachmentTimer = null;

            // A slow node never ends a session: a lagging lane buffers and catches up at one inference per window. Only the
            // memory cap ends it, gracefully, and the frame that crossed the cap is still queued, so nothing admitted is dropped.
            session.PendingBytes += owned.Length;
            session.ReceivedBytes += owned.Length;
            lane.QueuedBytes += owned.Length;
            lane.Chain = ConsumeAsync(session, lane, owned, lane.Chain);
            if (session.PendingBytes > _maxBufferedBytes && !session.BufferCapReached)
            {
                session.BufferCapReached = true;
                capReached = true;
            }
        }

        if (capReached)
        {
            _logger.LogWarning("Live transcription session {SessionId} buffered more than {CapMb} MB of untranscribed audio; stopping it gracefully.",
                sessionId,
                _options.MaxBufferedAudioMb);

            // Not awaited, and outside the gate: the caller is a capture callback, and ending drains lanes.
            _ = BeginEnd(session, LiveEndReason.Completed);
        }

        return Task.CompletedTask;
    }

    public Task EndAsync(Guid sessionId, LiveEndReason reason, CancellationToken cancellationToken) =>
        _sessions.TryGetValue(sessionId, out var session) ? BeginEnd(session, reason) : Task.CompletedTask;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The detach handle is the caller's: a producer disposes it when it stops on its own, and ending the session drops the reference either way.")]
    public LiveProducerRegistration AttachProducer(Guid sessionId, ILiveAudioProducer producer)
    {
        ArgumentNullException.ThrowIfNull(producer);

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException($"Transcription session {sessionId} is not live.");
        }

        lock (session.Gate)
        {
            // A session that has already closed admission may have run its producer-stop step, so a producer attached now would never be stopped by anything and
            // would keep capturing against a session that is gone. Refusing here is what makes "exactly one stop per producer" true.
            if (session.AdmissionClosed)
            {
                throw new InvalidOperationException($"Transcription session {sessionId} is not live.");
            }

            // Attaching satisfies the producer-attachment deadline; waiting for the first FRAME does not: a native capture of a silent application pushes nothing (WASAPI never
            // yields a silent packet), so a healthy running recorder would be reaped as NeverAttached. The browser abandonment grace is untouched: a closed tab ends the session whatever feeds it.
            session.AttachmentTimer?.Dispose();
            session.AttachmentTimer = null;

            session.Producer = producer;
        }

        return new LiveProducerRegistration
        {
            ProducerToken = session.ProducerCts.Token,
            Detach = new ProducerDetach(session, producer)
        };
    }

    public void NoteBrowserAttached(Guid sessionId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        lock (session.Gate)
        {
            _ = session.BrowserConnections.Add(connectionId);
            session.GraceTimer?.Dispose();
            session.GraceTimer = null;
        }
    }

    public void NoteBrowserDetached(Guid sessionId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        lock (session.Gate)
        {
            _ = session.BrowserConnections.Remove(connectionId);
            if (session.AdmissionClosed || session.BrowserConnections.Count > 0 || session.GraceTimer is not null)
            {
                return;
            }

            session.GraceTimer = _timeProvider.CreateTimer(EndOnTimer,
                new TimerState
                {
                    Registry = this,
                    Session = session,
                    Reason = LiveEndReason.Abandoned
                },
                TimeSpan.FromSeconds(_options.AbandonedSessionGraceSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

    public bool IsRegistered(Guid sessionId) =>
        _sessions.ContainsKey(sessionId);

    public bool IsLive(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return false;
        }

        lock (session.Gate)
        {
            return !session.AdmissionClosed;
        }
    }

    /// <summary>
    ///     Ends every live session as cancelled, with a bounded drain so a wedged lane cannot hold the process open.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var ending = _sessions.Values.Select(session => BeginEnd(session, LiveEndReason.Cancelled)).ToArray();
        if (ending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(ending).WaitAsync(ShutdownDrainTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Live transcription sessions did not finish ending within {Timeout}.", ShutdownDrainTimeout);
        }
    }

    private static void EndOnTimer(object? state)
    {
        var timer = (TimerState)state!;
        _ = timer.Registry.BeginEnd(timer.Session, timer.Reason);
    }

    // Completed, and only Completed, is worth one more inference: it is the one end where the audio the speaker just
    // produced is still wanted. Every other reason maps onto a status the row can carry.
    private static (TranscriptionSessionStatus Status, string? ErrorCode, string? ErrorMessage) MapReason(LiveEndReason reason) =>
        reason switch
        {
            LiveEndReason.Completed => (TranscriptionSessionStatus.Completed, null, null),
            LiveEndReason.Cancelled or LiveEndReason.Abandoned or LiveEndReason.NeverAttached =>
                (TranscriptionSessionStatus.Cancelled, null, null),
            LiveEndReason.Failed => (TranscriptionSessionStatus.Failed, "live-failed",
                "The live transcription lane stopped making progress."),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown live transcription end reason.")
        };

    private Task BeginEnd(LiveSession session, LiveEndReason reason)
    {
        Task ending;
        var escalate = false;
        lock (session.Gate)
        {
            if (session.EndTask is null)
            {
                // Admission closes synchronously; no frame can enter the final lane chains after this point.
                session.AdmissionClosed = true;
                session.EndReason = reason;
                session.EndTask = RunEndAsync(session, reason);
            }
            else if (!session.Finalized && session.EndReason == LiveEndReason.Completed && reason != LiveEndReason.Completed)
            {
                // The first abort replaces a pending graceful stop, never an already selected failure or cancellation.
                session.EndReason = reason;
                escalate = true;
            }

            ending = session.EndTask;
        }

        if (escalate)
        {
            _ = RequestCancellationAsync(session.Abort);
        }

        return ending;
    }

    private async Task RequestCancellationAsync(CancellationTokenSource cancellation)
    {
        try
        {
            // Outside Gate: cancellation callbacks may re-enter the registry.
            await cancellation.CancelAsync();
        }
#pragma warning disable CA1031 // A producer callback cannot prevent the already selected abort from terminalizing.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Cancelling live transcription resources failed.");
        }
    }

    private async Task RunEndAsync(LiveSession session, LiveEndReason reason)
    {
        // Off the caller's stack before anything observable runs: BeginEnd holds the session lock until this method's
        // first await, and cancelling a producer token invokes that producer's callbacks inline.
        await Task.Yield();

        try
        {
            DisarmTimers(session);

            if (reason == LiveEndReason.NeverAttached)
            {
                // The one end that says the client never delivered anything: a denied permission, a dead input device or
                // a capture graph that never started. Without this line a stuck browser leaves no trace on the node.
                _logger.LogWarning("Live transcription session {SessionId} received no audio frame and no producer attached within the {AttachmentTimeout} producer-attachment timeout; ending it.",
                    session.Id,
                    ProducerAttachmentTimeout);
            }

            if (reason == LiveEndReason.Completed)
            {
                // Before the producer stop, which may take seconds: the client shows the backlog from the moment it asks to stop.
                await AnnounceGracefulEndAsync(session);
            }
            else
            {
                // An abort must not wait behind an inference. Cancelling frees every lane at its next cancellation
                // point, which is what makes stopping prompt even while the transcriber is mid-call.
                _ = RequestCancellationAsync(session.Abort);
            }

            await StopProducerAsync(session);

            // A graceful end that could not finalize its last window is NOT a completed transcript: telling the operator otherwise hands them a success for a
            // recording that is missing its final seconds, and the retained audio is gone by then.
            var finalizedCleanly = await DrainAndFlushAsync(session, reason);

            // Freeze the winning reason together with the commit barrier. Cancellation before this point may
            // escalate a graceful stop; cancellation afterwards cannot rewrite a terminal result.
            var (outcome, gracefulFailure) = await FinalizeAsync(session, finalizedCleanly);
            var errorCode = gracefulFailure ? FlushFailedErrorCode : null;
            var errorMessage = gracefulFailure ? FlushFailedErrorMessage : null;

            await CompleteAsync(session, outcome, errorCode, errorMessage);
            await CloseProgressAsync(session);
            await _publisher.PublishStatusAsync(session.Id, outcome, CancellationToken.None);
            LogEnded(session, outcome);
        }
#pragma warning disable CA1031 // Ending a session is the last chance to release it; nothing above may escape.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogError(exception, "Ending live transcription session {SessionId} failed.", session.Id);
        }
        finally
        {
            _ = _sessions.TryRemove(session.Id, out _);
        }
    }

    private void LogEnded(LiveSession session, LiveEndReason outcome)
    {
        long receivedMs;
        long consumedMs;
        lock (session.Gate)
        {
            receivedMs = WavPcm16.DurationMs(session.ReceivedBytes);
            consumedMs = WavPcm16.DurationMs(session.ConsumedBytes);
        }

        // Finalized: no commit allocates a sequence any more, so Seq is stable without the commit gate.
        _logger.LogInformation(
            "Live transcription session {SessionId} ended: reason {EndReason}, status {Status}, audio received {ReceivedSeconds:F1} s, consumed {ConsumedSeconds:F1} s, {SegmentCount} segments committed, duration {Duration}.",
            session.Id,
            outcome,
            MapReason(outcome).Status,
            receivedMs / 1000.0,
            consumedMs / 1000.0,
            session.Seq - session.Options.StartingSeq,
            _timeProvider.GetUtcNow() - session.StartedAt);
    }

    /// <summary>
    ///     Publishes admission closed, then the drain-start backlog, as ONE progress-gate section. The snapshot is taken
    ///     inside it, so a lane finishing a frame meanwhile cannot report ahead of admission closed or be overtaken by a stale backlog.
    /// </summary>
    private async Task AnnounceGracefulEndAsync(LiveSession session)
    {
        await session.ProgressGate.WaitAsync(CancellationToken.None);
        try
        {
            // First, so the browser stops sending frames that admission would now drop silently.
            await _publisher.PublishAdmissionClosedAsync(session.Id, CancellationToken.None);

            long bufferedMs;
            lock (session.Gate)
            {
                bufferedMs = WavPcm16.DurationMs(session.PendingBytes);
                session.ReportedBehind = session.PendingBytes > 0;
                session.ConsumedWhileBehindBytes = 0;
            }

            await _publisher.PublishCatchUpAsync(session.Id, bufferedMs, CancellationToken.None);
        }
#pragma warning disable CA1031 // Progress is advisory; failing to announce it must not stop the graceful end.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Announcing the graceful end of transcription session {SessionId} failed.", session.Id);
        }
        finally
        {
            _ = session.ProgressGate.Release();
        }
    }

    private static void DisarmTimers(LiveSession session)
    {
        lock (session.Gate)
        {
            session.AttachmentTimer?.Dispose();
            session.AttachmentTimer = null;
            session.GraceTimer?.Dispose();
            session.GraceTimer = null;
        }
    }

    private async Task StopProducerAsync(LiveSession session)
    {
        ILiveAudioProducer? producer;
        lock (session.Gate)
        {
            producer = session.Producer;
            session.Producer = null;
        }

        // Cancelled BEFORE StopAsync is called: a producer that already stops on its token has nothing left to do.
        _ = RequestCancellationAsync(session.ProducerCts);

        if (producer is null)
        {
            return;
        }

        try
        {
            using var bound = new CancellationTokenSource(ProducerStopTimeout, _timeProvider);
            await producer.StopAsync(bound.Token).AsTask().WaitAsync(bound.Token);
        }
#pragma warning disable CA1031 // A producer that fails or hangs is abandoned, never allowed to hold the session.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "The audio producer for transcription session {SessionId} did not stop cleanly.", session.Id);
        }
    }

    /// <summary>
    ///     Drains each lane's queue and, on a graceful end only, flushes it. Returns whether every lane finalized
    ///     cleanly; false is what turns a <c>Completed</c> end into a <c>Failed</c> one.
    /// </summary>
    private async Task<bool> DrainAndFlushAsync(LiveSession session, LiveEndReason reason)
    {
        if (reason != LiveEndReason.Completed)
        {
            var aborted = session.Lanes.Values.Select(lane => FinalizeLaneAsync(session, lane, graceful: false, CancellationToken.None));
            _ = await Task.WhenAll(aborted);
            return true;
        }

        // No deadline: the operator asked for the whole transcript. Only an abort (Cancel, shutdown, a failed lane
        // escalating) interrupts it. Independent lanes finalize concurrently, but each lane drains before it flushes.
        var results = await Task.WhenAll(session.Lanes.Values.Select(lane => FinalizeLaneAsync(session, lane, graceful: true, session.Abort.Token)));
        return results.All(static clean => clean);
    }

    private async Task<bool> FinalizeLaneAsync(LiveSession session, Lane lane, bool graceful, CancellationToken stopToken)
    {
        Task chain;
        lock (session.Gate)
        {
            chain = lane.Chain;
        }

        try
        {
            if (graceful)
            {
                await chain.WaitAsync(stopToken);
                stopToken.ThrowIfCancellationRequested();
                // WaitAsync bounds even a runtime ignoring cancellation. Its late result still goes through the
                // commit barrier in FlushLaneAsync; never flush an undrained lane or race its segmenter state.
                await FlushLaneAsync(session, lane, stopToken).WaitAsync(stopToken);
            }
            else
            {
                // Abort already cancelled inference. Retain the short cleanup bound without re-submitting audio.
                await chain.WaitAsync(LaneDrainTimeout, _timeProvider, CancellationToken.None);
            }

            return true;
        }
#pragma warning disable CA1031 // A timed-out or faulted lane must not stop siblings finalizing or the terminal status.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _ = RequestCancellationAsync(lane.Abort);
            _logger.LogWarning(exception, "Finalizing the {Channel} lane of transcription session {SessionId} did not finish cleanly.", lane.Channel, session.Id);
            return !graceful;
        }
    }

    private async Task FlushLaneAsync(LiveSession session, Lane lane, CancellationToken stopToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lane.Abort.Token, stopToken);
        var tick = await lane.Segmenter.FlushAsync(cancellation.Token);
        await CommitAsync(session, lane, tick);
        NoteProgress(session, lane, tick);
    }

    /// <summary>Closes the commit pipeline and freezes the terminal outcome under the same barrier.</summary>
    private static async Task<(LiveEndReason Reason, bool GracefulFailure)> FinalizeAsync(LiveSession session, bool finalizedCleanly)
    {
        await session.CommitGate.WaitAsync(CancellationToken.None);
        try
        {
            lock (session.Gate)
            {
                session.Finalized = true;

                // EndReason is written under this same gate, together with EndTask, before the task that runs this
                // method exists; reaching here unset would mean the end pipeline started without an end.
                var selected = session.EndReason
                               ?? throw new InvalidOperationException("The live transcription session ended without a reason.");
                var gracefulFailure = selected == LiveEndReason.Completed && !finalizedCleanly;
                return (gracefulFailure ? LiveEndReason.Failed : selected, gracefulFailure);
            }
        }
        finally
        {
            _ = session.CommitGate.Release();
        }
    }

    private async Task CompleteAsync(LiveSession session, LiveEndReason reason, string? errorCodeOverride, string? errorMessageOverride)
    {
        if (!session.Options.Persist)
        {
            return;
        }

        var (status, mappedCode, mappedMessage) = MapReason(reason);
        var errorCode = errorCodeOverride ?? mappedCode;
        var errorMessage = errorMessageOverride ?? mappedMessage;
        long durationMs;
        string? detectedLanguage;
        lock (session.Gate)
        {
            durationMs = session.AudioEndMs;
            detectedLanguage = session.DetectedLanguage;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ITranscriptionService>()
                   .CompleteLiveAsync(session.Id, status, durationMs, detectedLanguage, errorCode, errorMessage, CancellationToken.None);
    }

    private async Task ConsumeAsync(LiveSession session, Lane lane, byte[] pcm16, Task previous)
    {
        // Off the caller's stack before anything runs: the chain is appended under session.Gate, and a body that began synchronously would hold that gate across a
        // segmenter call. Ordering is unaffected — `previous` was captured when this frame was admitted.
        await Task.Yield();

        try
        {
            // Ordering, not a result: the previous frame's own handler already dealt with however it ended.
            await previous;
        }
#pragma warning disable CA1031 // See above: this await exists only to keep frames in arrival order.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Intentionally ignored.
        }

        bool catchingUp;
        var consumed = false;
        long? progress = null;
        lock (session.Gate)
        {
            // Another frame is already queued behind this one: the lane is behind real time.
            catchingUp = lane.QueuedBytes > pcm16.Length;
        }

        try
        {
            var tick = await lane.Segmenter.PushAsync(pcm16, catchingUp, lane.Abort.Token);
            await CommitAsync(session, lane, tick);
            NoteProgress(session, lane, tick);
            consumed = true;
        }
        catch (OperationCanceledException)
        {
            // The session is already ending; the retained audio is deliberately not re-submitted.
        }
#pragma warning disable CA1031 // A lane failure terminalizes the session instead of unwinding into a capture callback.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogError(exception, "The {Channel} lane of transcription session {SessionId} failed.", lane.Channel, session.Id);

            // Not awaited: ending drains this very chain, so awaiting here would wait on itself.
            _ = BeginEnd(session, LiveEndReason.Failed);
        }
        finally
        {
            lock (session.Gate)
            {
                session.PendingBytes -= pcm16.Length;
                lane.QueuedBytes -= pcm16.Length;
                if (consumed)
                {
                    session.ConsumedBytes += pcm16.Length;
                }

                // Progress counts audio transcribed, not audio discarded: an abort unwinding its backlog reports nothing.
                if (consumed && !session.Abort.IsCancellationRequested)
                {
                    progress = NextCatchUpReport(session, lane, pcm16.Length);
                }
            }
        }

        if (progress is { } bufferedMs)
        {
            await PublishCatchUpAsync(session, bufferedMs);
        }
    }

    /// <summary>Decides, under the session gate, whether consuming one frame is worth a catch-up report.</summary>
    private static long? NextCatchUpReport(LiveSession session, Lane lane, int consumedBytes)
    {
        if (session.PendingBytes == 0)
        {
            if (!session.ReportedBehind)
            {
                return null;
            }

            session.ReportedBehind = false;
            session.ConsumedWhileBehindBytes = 0;
            return 0;
        }

        // Only a lane with its own queue is behind; a sibling's frame merely in flight is not lag worth reporting.
        if (lane.QueuedBytes == 0)
        {
            return null;
        }

        session.ConsumedWhileBehindBytes += consumedBytes;
        if (session.ConsumedWhileBehindBytes < CatchUpReportBytes)
        {
            return null;
        }

        session.ConsumedWhileBehindBytes = 0;
        session.ReportedBehind = true;
        return WavPcm16.DurationMs(session.PendingBytes);
    }

    /// <summary>
    ///     Publishes catch-up progress under its own gate, never the commit gate: a lane with nothing to commit must not
    ///     wait behind a sibling's persistence. <see cref="CloseProgressAsync" /> shuts it before the terminal status.
    /// </summary>
    private Task PublishCatchUpAsync(LiveSession session, long bufferedMs) =>
        PublishProgressAsync(session, () => _publisher.PublishCatchUpAsync(session.Id, bufferedMs, CancellationToken.None), "catch-up progress");

    private async Task PublishProgressAsync(LiveSession session, Func<Task> publish, string what)
    {
        await session.ProgressGate.WaitAsync(CancellationToken.None);
        try
        {
            if (!session.ProgressClosed)
            {
                await publish();
            }
        }
#pragma warning disable CA1031 // Progress is advisory; failing to report it must not fail the lane that consumed the audio.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            _logger.LogWarning(exception, "Publishing {Event} for transcription session {SessionId} failed.", what, session.Id);
        }
        finally
        {
            _ = session.ProgressGate.Release();
        }
    }

    private static async Task CloseProgressAsync(LiveSession session)
    {
        await session.ProgressGate.WaitAsync(CancellationToken.None);
        session.ProgressClosed = true;
        _ = session.ProgressGate.Release();
    }

    private async Task CommitAsync(LiveSession session, Lane lane, LiveTick tick)
    {
        foreach (var commit in tick.Commits)
        {
            // CancellationToken.None throughout: a commit that has been allocated a sequence must reach the database
            // and the socket, or the client's watermark skips a number it will never see again.
            await session.CommitGate.WaitAsync(CancellationToken.None);
            try
            {
                if (IsFinalized(session))
                {
                    _logger.LogWarning("Dropping a {Channel} commit ({StartMs}-{EndMs} ms) that arrived after transcription session {SessionId} was finalized.",
                        commit.Channel,
                        commit.StartMs,
                        commit.EndMs,
                        session.Id);
                    continue;
                }

                var seq = ++session.Seq;

                if (session.Options.Persist)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ITranscriptionService>()
                               .AppendLiveSegmentAsync(session.Id,
                                   seq,
                                   commit.Channel,
                                   commit.StartMs,
                                   commit.EndMs,
                                   commit.Text,
                                   commit.Confidence,
                                   CancellationToken.None);
                }

                await _publisher.PublishSegmentAsync(session.Id,
                    seq,
                    commit.Channel,
                    commit.StartMs,
                    commit.EndMs,
                    commit.Text,
                    commit.Confidence,
                    CancellationToken.None);
            }
            finally
            {
                _ = session.CommitGate.Release();
            }
        }

        if (string.Equals(tick.Partial, lane.LastPartial, StringComparison.Ordinal))
        {
            return;
        }

        // Under the commit gate, exactly like a segment: checking finalization outside it left a window in which a
        // lane could pass the check, pause, and publish provisional text after the terminal status had gone out.
        await session.CommitGate.WaitAsync(CancellationToken.None);
        try
        {
            if (IsFinalized(session))
            {
                return;
            }

            lane.LastPartial = tick.Partial;
            await _publisher.PublishPartialAsync(session.Id, lane.Channel, tick.Partial, CancellationToken.None);
        }
        finally
        {
            _ = session.CommitGate.Release();
        }
    }

    private static bool IsFinalized(LiveSession session)
    {
        lock (session.Gate)
        {
            return session.Finalized;
        }
    }

    private static void NoteProgress(LiveSession session, Lane lane, LiveTick tick)
    {
        lock (session.Gate)
        {
            session.AudioEndMs = Math.Max(session.AudioEndMs, lane.Segmenter.AudioEndMs);
            session.DetectedLanguage ??= tick.DetectedLanguage;
        }
    }

    /// <summary>The state an armed timer carries, so the callback closes over nothing.</summary>
    private sealed record TimerState
    {
        public required LiveTranscriptionSessionRegistry Registry { get; init; }

        public required LiveSession Session { get; init; }

        public required LiveEndReason Reason { get; init; }
    }

    /// <summary>Detaches one producer, and only if it is still the attached one.</summary>
    private sealed class ProducerDetach : IDisposable
    {
        private readonly LiveSession _session;
        private readonly ILiveAudioProducer _producer;

        public ProducerDetach(LiveSession session, ILiveAudioProducer producer)
        {
            _session = session;
            _producer = producer;
        }

        public void Dispose()
        {
            lock (_session.Gate)
            {
                if (ReferenceEquals(_session.Producer, _producer))
                {
                    _session.Producer = null;
                }
            }
        }
    }

    /// <summary>One channel of one session: its segmenter, its frame queue and its last provisional text.</summary>
    private sealed class Lane
    {
        public Lane(TranscriptChannel channel, LiveTranscriptionSegmenter segmenter, CancellationTokenSource abort)
        {
            Channel = channel;
            Segmenter = segmenter;
            Abort = abort;
            Chain = Task.CompletedTask;
        }

        public TranscriptChannel Channel { get; }

        public LiveTranscriptionSegmenter Segmenter { get; }

        /// <summary>
        ///     This lane's cancellation, linked to the session's. Cancelling the session cancels every lane; a lane
        ///     whose finalization faulted is cancelled alone, so a sibling that drained cleanly can still flush the
        ///     speech it was holding.
        /// </summary>
        public CancellationTokenSource Abort { get; }

        /// <summary>Every queued frame, linked.</summary>
        /// <remarks>
        ///     A chain rather than a semaphore because ordering is load-bearing here: the lane derives its clock from
        ///     the cumulative byte count, so two frames swapped rewrite the timeline, and
        ///     <see cref="SemaphoreSlim" /> does not promise the order its waiters are released in. Read and written
        ///     only under the owning session's <c>Gate</c>, together with the admission check.
        /// </remarks>
        public Task Chain { get; set; }

        /// <summary>This lane's share of the session's <c>PendingBytes</c>. Guarded by the owning session's <c>Gate</c>.</summary>
        public long QueuedBytes { get; set; }

        /// <summary>Read and written only from this lane's own chain.</summary>
        public string LastPartial { get; set; } = string.Empty;
    }

    /// <summary>
    ///     One live session. Nothing here is disposed on the way out except the timers: a cancellation source or a
    ///     semaphore a racing frame still holds a reference to is cheaper to let the collector take than to guard.
    /// </summary>
    private sealed class LiveSession
    {
        /// <summary>Retained PCM across every lane, in bytes. Guarded by <see cref="Gate" />, like the queue itself.</summary>
        public long PendingBytes;

        /// <summary>Every admitted byte across lanes, for the end-of-session log line. Guarded by <see cref="Gate" />.</summary>
        public long ReceivedBytes;

        /// <summary>Admitted bytes a lane actually transcribed. Guarded by <see cref="Gate" />.</summary>
        public long ConsumedBytes;

        public required Guid Id { get; init; }

        /// <summary>Registration time, from the registry's clock.</summary>
        public required DateTimeOffset StartedAt { get; init; }

        public required LiveSessionOptions Options { get; init; }

        /// <summary>Cancels every lane at once when the session ends for any reason but a graceful one.</summary>
        public required CancellationTokenSource Abort { get; init; }

        public required IReadOnlyDictionary<TranscriptChannel, Lane> Lanes { get; init; }

        /// <summary>Set once the buffered-audio cap ended the session, so it warns once. Guarded by <see cref="Gate" />.</summary>
        public bool BufferCapReached { get; set; }

        /// <summary>Audio consumed while behind since the last catch-up report. Guarded by <see cref="Gate" />.</summary>
        public long ConsumedWhileBehindBytes { get; set; }

        /// <summary>Whether a non-zero backlog was reported and the caught-up <c>0</c> is still owed. Guarded by <see cref="Gate" />.</summary>
        public bool ReportedBehind { get; set; }

        /// <summary>Allocation, persistence and publication of one commit are one critical section under this.</summary>
        public SemaphoreSlim CommitGate { get; } = new(initialCount: 1, maxCount: 1);

        /// <summary>Orders catch-up reports against the terminal status; guards <see cref="ProgressClosed" />.</summary>
        public SemaphoreSlim ProgressGate { get; } = new(initialCount: 1, maxCount: 1);

        /// <summary>Set just before the terminal status push; no catch-up report is sent after it.</summary>
        public bool ProgressClosed { get; set; }

        /// <summary>The token an in-host producer stops on.</summary>
        public CancellationTokenSource ProducerCts { get; } = new();

        /// <summary>Guards every mutable member below, plus <see cref="Seq" />'s companions.</summary>
        public Lock Gate { get; } = new();

        public HashSet<string> BrowserConnections { get; } = new(StringComparer.Ordinal);

        public ILiveAudioProducer? Producer { get; set; }

        public ITimer? AttachmentTimer { get; set; }

        public ITimer? GraceTimer { get; set; }

        public bool AdmissionClosed { get; set; }

        public Task? EndTask { get; set; }

        /// <summary>
        ///     Null until an end is selected. Nullable because <see cref="LiveEndReason.Completed" /> is the zero
        ///     value: a non-nullable field would make a session that has not ended read as a graceful completion,
        ///     which is exactly what the escalation guard in <c>BeginEnd</c> tests for.
        /// </summary>
        public LiveEndReason? EndReason { get; set; }

        /// <summary>
        ///     Set once the terminal status is about to be written. A commit that arrives after it is dropped: the
        ///     client has already been told the session is over, and a later row would never be replayed to it.
        /// </summary>
        public bool Finalized { get; set; }

        /// <summary>The last allocated sequence. Guarded by <see cref="CommitGate" />, never by <see cref="Gate" />.</summary>
        public long Seq { get; set; }

        /// <summary>How much audio the furthest lane has received; the session's duration when it ends.</summary>
        public long AudioEndMs { get; set; }

        public string? DetectedLanguage { get; set; }
    }
}
