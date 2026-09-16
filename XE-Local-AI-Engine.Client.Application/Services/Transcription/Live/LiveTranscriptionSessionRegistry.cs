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
///     <para>
///         <b>A lane is a queue, not a call.</b> <see cref="PushAudioAsync" /> appends the frame to that lane's task
///         chain and returns; the chain is what serializes inference and what keeps frames in arrival order. A capture
///         callback that waited for the transcriber would drop audio at the source, which is the one place the engine
///         can never get it back from.
///     </para>
///     <para>
///         <b>Every commit crosses one session-wide lock.</b> Allocating the sequence, persisting the row and
///         publishing the push are one critical section, because the client's watermark depends on publication order:
///         with allocation alone serialized, a lane that stalled inside its write would publish sequence two before
///         sequence one and the client would discard the first segment permanently.
///     </para>
///     <para>
///         <b>Persistence is a subscriber of a commit, never its source.</b> A persist-free session emits the
///         identical event sequence with no row behind it, so nothing here may make a publish conditional on a write.
///     </para>
/// </remarks>
public sealed class LiveTranscriptionSessionRegistry : ILiveTranscriptionSessionRegistry, IAsyncDisposable
{
    /// <summary>The ceiling on retained PCM per session, whatever the window size works out to.</summary>
    public const int MaxPendingBytes = 640 * 1024;

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

    // Safety bounds, never a wait: in every ordinary end both are satisfied immediately. They exist so one wedged
    // lane or one wedged shutdown cannot hold the session, or the process, open forever.
    private static readonly TimeSpan LaneDrainTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();
    private readonly IWhisperTranscriber _transcriber;
    private readonly ITranscriptionEventPublisher _publisher;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly TranscriptionOptions _options;
    private readonly ILogger<LiveTranscriptionSessionRegistry> _logger;
    private bool _disposed;

    /// <summary>Creates the registry.</summary>
    /// <param name="transcriber">The runtime every lane submits its windows to.</param>
    /// <param name="publisher">Where commits, partials and the final status go.</param>
    /// <param name="scopeFactory">
    ///     Resolves <see cref="ITranscriptionService" /> at call time. Injecting it would close a constructor cycle:
    ///     the service resolves this registry so cancel and delete can route through one termination path.
    /// </param>
    /// <param name="options">Carries the abandonment grace.</param>
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
                                       options.Settings),
                                   CancellationTokenSource.CreateLinkedTokenSource(abort.Token)));

        var session = new LiveSession
        {
            Id = sessionId,
            Abort = abort,
            Options = options,
            Lanes = lanes,
            // Two windows of audio, so a lane one window behind is tolerated and a lane two behind is not.
            PendingBudgetBytes = Math.Min((long)options.Settings.MaxWindowSeconds * 2 * WavPcm16.SampleRate * 2, MaxPendingBytes),
            Seq = options.StartingSeq
        };

        if (!_sessions.TryAdd(sessionId, session))
        {
            throw new LiveSessionAlreadyRegisteredException(sessionId);
        }

        lock (session.Gate)
        {
            session.AttachmentTimer = _timeProvider.CreateTimer(EndOnTimer,
                new TimerState(this, session, LiveEndReason.NeverAttached),
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

        // Copied before it is queued, and never referenced again. This method returns while the frame is still in
        // the lane's queue, so an in-process capture source is free to reuse or return its buffer the moment it
        // gets control back; holding the caller's memory would put replacement audio in the transcript. The hub
        // path is already safe (SignalR hands over a fresh array per invocation) — the in-process seam is not.
        var owned = pcm16.ToArray();
        var overloaded = false;

        // Admission, the budget and the queue insertion are ONE critical section, taken on the same gate BeginEnd
        // closes admission under. Splitting them let a push pass the admission check, pause, and then append to a
        // lane whose chain termination had already snapshotted — running a second concurrent call into a segmenter
        // that is single-threaded by contract, after the session had been declared over.
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

            // Accounted BEFORE the frame is queued. The lane holds its chain across inference, so a transcriber
            // slower than real time otherwise accumulates invocations and PCM without any limit at all.
            if (session.PendingBytes + owned.Length > session.PendingBudgetBytes)
            {
                overloaded = true;
            }
            else
            {
                session.PendingBytes += owned.Length;
                lane.Chain = ConsumeAsync(session, lane, owned, lane.Chain);
            }
        }

        if (overloaded)
        {
            // Not awaited, and outside the gate: the caller is a capture callback, and ending drains lanes. The
            // frame is refused rather than silently dropped — the session fails visibly instead of going quiet.
            _ = BeginEnd(session, LiveEndReason.Overloaded);
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
            // A session that has already closed admission may have run its producer-stop step, so a producer
            // attached now would never be stopped by anything: it would keep capturing against a session that is
            // gone. Refusing here is what makes "exactly one stop per producer" true.
            if (session.AdmissionClosed)
            {
                throw new InvalidOperationException($"Transcription session {sessionId} is not live.");
            }

            // Attaching satisfies the producer-attachment deadline; waiting for the first FRAME does not. A native
            // capture of an application that happens to be silent pushes nothing — WASAPI never yields a silent
            // packet at all — so a session with a healthy running recorder would be reaped as NeverAttached once
            // the deadline elapsed. The browser abandonment grace is untouched: a closed tab still ends the
            // session whatever else is feeding it.
            session.AttachmentTimer?.Dispose();
            session.AttachmentTimer = null;

            session.Producer = producer;
        }

        return new LiveProducerRegistration(session.ProducerCts.Token, new ProducerDetach(session, producer));
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
                new TimerState(this, session, LiveEndReason.Abandoned),
                TimeSpan.FromSeconds(_options.AbandonedSessionGraceSeconds),
                Timeout.InfiniteTimeSpan);
        }
    }

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
            await Task.WhenAll(ending).WaitAsync(ShutdownDrainTimeout).ConfigureAwait(false);
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
            LiveEndReason.Overloaded => (TranscriptionSessionStatus.Failed, "live-overloaded",
                "Audio arrived faster than this node could transcribe it."),
            LiveEndReason.Failed => (TranscriptionSessionStatus.Failed, "live-failed",
                "The live transcription lane stopped making progress."),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown live transcription end reason.")
        };

    private Task BeginEnd(LiveSession session, LiveEndReason reason)
    {
        lock (session.Gate)
        {
            if (session.EndTask is not null)
            {
                return session.EndTask;
            }

            // Admission closes HERE, synchronously, so a frame racing the rest of the teardown cannot reach a lane.
            session.AdmissionClosed = true;
            session.EndTask = RunEndAsync(session, reason);
            return session.EndTask;
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

            if (reason != LiveEndReason.Completed)
            {
                // An abort must not wait behind an inference. Cancelling frees every lane at its next cancellation
                // point, which is what makes stopping prompt even while the transcriber is mid-call.
                await session.Abort.CancelAsync().ConfigureAwait(false);
            }

            await StopProducerAsync(session).ConfigureAwait(false);

            // A graceful end that could not finalize its last window is NOT a completed transcript. Telling the
            // operator otherwise hands them a success for a recording that is missing its final seconds, and the
            // retained audio is gone by then.
            var outcome = await DrainAndFlushAsync(session, reason).ConfigureAwait(false)
                ? reason
                : LiveEndReason.Failed;
            var errorCode = outcome == reason ? null : FlushFailedErrorCode;
            var errorMessage = outcome == reason ? null : FlushFailedErrorMessage;

            // The point of no return, taken THROUGH the commit gate: a commit already inside the pipeline finishes,
            // and no commit may start after it. An abandoned lane that answers later is dropped rather than
            // persisted and published for a session the client has been told is over.
            await FinalizeAsync(session).ConfigureAwait(false);

            await CompleteAsync(session, outcome, errorCode, errorMessage).ConfigureAwait(false);
            await _publisher.PublishStatusAsync(session.Id, outcome, CancellationToken.None).ConfigureAwait(false);
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
        await session.ProducerCts.CancelAsync().ConfigureAwait(false);

        if (producer is null)
        {
            return;
        }

        try
        {
            using var bound = new CancellationTokenSource(ProducerStopTimeout, _timeProvider);
            await producer.StopAsync(bound.Token).AsTask().WaitAsync(bound.Token).ConfigureAwait(false);
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
        var finalizedCleanly = true;

        foreach (var lane in session.Lanes.Values)
        {
            // Snapshotted under the same gate admission closes under, so the chain read here is final: nothing can
            // append to it after this point.
            Task chain;
            lock (session.Gate)
            {
                chain = lane.Chain;
            }

            // The bound runs off the injected clock, so a test drives it and production still gets five real
            // seconds. Timing out means the lane is STILL RUNNING, which is the one case the flush below must not
            // race: a segmenter is single-threaded by contract, so flushing a lane whose PushAsync has not returned
            // would corrupt the very buffer the flush is meant to finalize.
            var drained = true;
            try
            {
                // Explicitly not propagating: this drain is what the abort tokens already triggered, so passing
                // session.Abort.Token or lane.Abort.Token would abandon the lane the flush below must not race.
                await chain.WaitAsync(LaneDrainTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                drained = false;
                _logger.LogWarning("The {Channel} lane of transcription session {SessionId} did not drain within {Timeout}; it is abandoned unflushed.",
                    lane.Channel,
                    session.Id,
                    LaneDrainTimeout);
            }
#pragma warning disable CA1031 // A faulted chain has still FINISHED; its own handler already reported why.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _logger.LogWarning(exception, "Draining the {Channel} lane of transcription session {SessionId} failed.", lane.Channel, session.Id);
            }

            if (!drained)
            {
                // THIS lane only. Cancelling the session's source here would hand a cancelled token to a sibling
                // that drained cleanly, so one wedged lane would discard another lane's flushable speech.
                await lane.Abort.CancelAsync().ConfigureAwait(false);
                finalizedCleanly &= reason != LiveEndReason.Completed;
                continue;
            }

            if (reason != LiveEndReason.Completed)
            {
                // Aborted: the token above already cancelled the in-flight submission, and re-submitting the retained
                // audio would be exactly the wait this branch exists to avoid.
                continue;
            }

            try
            {
                var tick = await lane.Segmenter.FlushAsync(lane.Abort.Token).ConfigureAwait(false);
                await CommitAsync(session, lane, tick).ConfigureAwait(false);
                NoteProgress(session, lane, tick);
            }
#pragma warning disable CA1031 // A lane that cannot finalize must not stop the session ending or the status push.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                finalizedCleanly = false;
                _logger.LogWarning(exception, "Flushing the {Channel} lane of transcription session {SessionId} failed.", lane.Channel, session.Id);
            }
        }

        return finalizedCleanly;
    }

    /// <summary>
    ///     Closes the commit pipeline. Taken through the commit gate so a commit already inside it finishes first.
    /// </summary>
    private static async Task FinalizeAsync(LiveSession session)
    {
        await session.CommitGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (session.Gate)
            {
                session.Finalized = true;
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
                   .CompleteLiveAsync(session.Id, status, durationMs, detectedLanguage, errorCode, errorMessage, CancellationToken.None)
                   .ConfigureAwait(false);
    }

    private async Task ConsumeAsync(LiveSession session, Lane lane, byte[] pcm16, Task previous)
    {
        // Off the caller's stack before anything runs: the chain is appended under session.Gate, and a body that
        // began synchronously would hold that gate across a segmenter call. Ordering is unaffected — `previous` was
        // captured when this frame was admitted.
        await Task.Yield();

        try
        {
            // Ordering, not a result: the previous frame's own handler already dealt with however it ended.
            await previous.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // See above: this await exists only to keep frames in arrival order.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Intentionally ignored.
        }

        try
        {
            var tick = await lane.Segmenter.PushAsync(pcm16, lane.Abort.Token).ConfigureAwait(false);
            await CommitAsync(session, lane, tick).ConfigureAwait(false);
            NoteProgress(session, lane, tick);
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
            }
        }
    }

    private async Task CommitAsync(LiveSession session, Lane lane, LiveTick tick)
    {
        foreach (var commit in tick.Commits)
        {
            // CancellationToken.None throughout: a commit that has been allocated a sequence must reach the database
            // and the socket, or the client's watermark skips a number it will never see again.
            await session.CommitGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
                                   CancellationToken.None)
                               .ConfigureAwait(false);
                }

                await _publisher.PublishSegmentAsync(session.Id,
                    seq,
                    commit.Channel,
                    commit.StartMs,
                    commit.EndMs,
                    commit.Text,
                    commit.Confidence,
                    CancellationToken.None).ConfigureAwait(false);
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
        await session.CommitGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (IsFinalized(session))
            {
                return;
            }

            lane.LastPartial = tick.Partial;
            await _publisher.PublishPartialAsync(session.Id, lane.Channel, tick.Partial, CancellationToken.None).ConfigureAwait(false);
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
    private sealed record TimerState(LiveTranscriptionSessionRegistry Registry, LiveSession Session, LiveEndReason Reason);

    /// <summary>Detaches one producer, and only if it is still the attached one.</summary>
    private sealed class ProducerDetach(LiveSession session, ILiveAudioProducer producer) : IDisposable
    {
        public void Dispose()
        {
            lock (session.Gate)
            {
                if (ReferenceEquals(session.Producer, producer))
                {
                    session.Producer = null;
                }
            }
        }
    }

    /// <summary>One channel of one session: its segmenter, its frame queue and its last provisional text.</summary>
    private sealed class Lane(TranscriptChannel channel, LiveTranscriptionSegmenter segmenter, CancellationTokenSource abort)
    {
        public TranscriptChannel Channel { get; } = channel;

        public LiveTranscriptionSegmenter Segmenter { get; } = segmenter;

        /// <summary>
        ///     This lane's cancellation, linked to the session's. Cancelling the session cancels every lane; a lane
        ///     that misses its drain deadline is cancelled alone, so a sibling that drained cleanly can still flush
        ///     the speech it was holding.
        /// </summary>
        public CancellationTokenSource Abort { get; } = abort;

        /// <summary>
        ///     Every queued frame, linked. A chain rather than a semaphore because ordering is load-bearing here: the
        ///     lane derives its clock from the cumulative byte count, so two frames swapped rewrite the timeline, and
        ///     <see cref="SemaphoreSlim" /> does not promise the order its waiters are released in.
        ///     <para>Read and written only under the owning session's <c>Gate</c>, together with the admission check.</para>
        /// </summary>
        public Task Chain { get; set; } = Task.CompletedTask;

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

        public required Guid Id { get; init; }

        public required LiveSessionOptions Options { get; init; }

        /// <summary>Cancels every lane at once when the session ends for any reason but a graceful one.</summary>
        public required CancellationTokenSource Abort { get; init; }

        public required IReadOnlyDictionary<TranscriptChannel, Lane> Lanes { get; init; }

        public required long PendingBudgetBytes { get; init; }

        /// <summary>Allocation, persistence and publication of one commit are one critical section under this.</summary>
        public SemaphoreSlim CommitGate { get; } = new(initialCount: 1, maxCount: 1);

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
