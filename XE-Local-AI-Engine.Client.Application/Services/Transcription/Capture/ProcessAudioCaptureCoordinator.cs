namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

using System.Collections.Concurrent;

/// <summary>Why a request to start per-application capture was, or was not, honoured.</summary>
public enum StartProcessCaptureOutcome
{
    /// <summary>Capture is attached and running.</summary>
    Started = 0,

    /// <summary>This host cannot capture process audio at all.</summary>
    NotSupported = 1,

    /// <summary>The session has no lanes yet: the live start has not run, or the session already ended.</summary>
    SessionNotLive = 2,

    /// <summary>This session already has a capture running; stop it before starting another.</summary>
    AlreadyCapturing = 3
}

/// <summary>
///     Runs per-application capture on tasks that outlive the request which started them — the posture
///     <c>IImageJobCoordinator</c> takes, for the same reason: a cancelled HTTP request must not kill a capture the
///     operator is still recording into.
/// </summary>
/// <remarks>
///     The coordinator is not itself the producer: <see cref="ILiveAudioProducer.StopAsync" /> carries no session id, so each
///     session gets its own <c>SessionCapture</c> handle, owning that session's linked cancellation source, capture task and
///     detach handle, and that is what <see cref="ILiveTranscriptionSessionRegistry.AttachProducer" /> receives. Cancelling the
///     registry's <c>ProducerToken</c> is the ONE stop signal, so a private token the registry cannot reach is how a recorder
///     outlives its session. This class never ends a session: that is the registry's single <c>EndAsync</c> path.
/// </remarks>
public sealed class ProcessAudioCaptureCoordinator : IAsyncDisposable
{
    // A bound, never a wait: in every ordinary shutdown the captures have already ended. It exists so one wedged
    // capture cannot hold the process open.
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<Guid, SessionCapture> _captures = new();
    private readonly IProcessAudioCaptureSource _source;
    private readonly ILiveTranscriptionSessionRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessAudioCaptureCoordinator> _logger;
    private int _disposed;

    /// <summary>Creates the coordinator.</summary>
    /// <param name="source">The capture implementation this host resolved to.</param>
    /// <param name="registry">The live-session registry: liveness, producer attachment and the audio seam.</param>
    /// <param name="timeProvider">Bounds the shutdown drain; there is no timer and no hosted service.</param>
    /// <param name="logger">A capture loop is detached, so a failure is only ever visible here.</param>
    public ProcessAudioCaptureCoordinator(IProcessAudioCaptureSource source,
        ILiveTranscriptionSessionRegistry registry,
        TimeProvider timeProvider,
        ILogger<ProcessAudioCaptureCoordinator> logger)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether this session currently has a capture attached.</summary>
    public bool IsCapturing(Guid sessionId) =>
        _captures.ContainsKey(sessionId);

    /// <summary>Attaches as this session's producer and starts capturing the process on a detached task.</summary>
    /// <remarks>
    ///     Synchronous because nothing here performs I/O — and a <c>Task</c>-returning signature would suggest it
    ///     waits for the capture, which is exactly what it must not do.
    /// </remarks>
    public StartProcessCaptureOutcome Start(Guid sessionId, int processId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        if (!_source.IsSupported)
        {
            return StartProcessCaptureOutcome.NotSupported;
        }

        // Asked before attaching so the caller gets a typed refusal rather than a recorder with nowhere to push.
        // The registry re-checks under its own lock, which is what actually closes the race; see below.
        if (!_registry.IsLive(sessionId))
        {
            return StartProcessCaptureOutcome.SessionNotLive;
        }

        var capture = new SessionCapture(this, sessionId, processId);
        if (!_captures.TryAdd(sessionId, capture))
        {
            return StartProcessCaptureOutcome.AlreadyCapturing;
        }

        try
        {
            capture.Attach();
        }
        catch (InvalidOperationException exception)
        {
            // The session stopped being live between the check above and the attach. The registry refuses a
            // producer once admission is closed, precisely so "exactly one stop per producer" stays true.
            _ = _captures.TryRemove(new KeyValuePair<Guid, SessionCapture>(sessionId, capture));
            _logger.LogInformation(exception, "Transcription session {SessionId} stopped being live before capture attached.", sessionId);
            return StartProcessCaptureOutcome.SessionNotLive;
        }

        return StartProcessCaptureOutcome.Started;
    }

    /// <summary>
    ///     Stops this session's capture and detaches it. Returns whether there was one. It does <b>not</b> end the
    ///     live session: that is the registry's single termination path, reached through the cancel endpoint.
    /// </summary>
    public async ValueTask<bool> StopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_captures.TryGetValue(sessionId, out var capture))
        {
            return false;
        }

        await capture.StopAsync(cancellationToken);
        return true;
    }

    /// <summary>Stops and detaches every active capture, with a bounded drain.</summary>
    public async ValueTask DisposeAsync()
    {
        // Interlocked, so two concurrent disposals cannot both run the drain.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var captures = _captures.Values.ToArray();
        foreach (var capture in captures)
        {
            await capture.StopAsync(CancellationToken.None);
        }

        try
        {
            // real-timer: a bound on shutdown, not a wait for an event. It runs off the injected clock, so a test
            // drives it and production still gets three real seconds.
            await Task.WhenAll(captures.Select(capture => capture.Capture))
                      .WaitAsync(ShutdownDrainTimeout, _timeProvider);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("One or more per-application captures did not stop within {Timeout}; they are abandoned.", ShutdownDrainTimeout);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A per-application capture failed while the coordinator was shutting down.");
        }
    }

    /// <summary>
    ///     One session's capture: the handle the registry actually holds. Separate from the coordinator because
    ///     <see cref="ILiveAudioProducer.StopAsync" /> has no session parameter.
    /// </summary>
    private sealed class SessionCapture : ILiveAudioProducer
    {
        // One lock, because publishing the registration and tearing it down are the same critical section. Without it a stop could
        // cancel nothing, then dispose the source Attach had just published, and Attach would report SessionNotLive for a live capture.
        private readonly Lock _gate = new();
        private readonly ProcessAudioCaptureCoordinator _owner;
        private readonly Guid _sessionId;
        private readonly int _processId;

        private CancellationTokenSource? _cancellation;
        private IDisposable? _detach;
        private bool _stopRequested;
        private bool _cleaned;

        public SessionCapture(ProcessAudioCaptureCoordinator owner, Guid sessionId, int processId)
        {
            _owner = owner;
            _sessionId = sessionId;
            _processId = processId;
        }

        /// <summary>The detached capture task. Never faults: every failure is caught and logged by the loop.</summary>
        internal Task Capture { get; private set; } = Task.CompletedTask;

        /// <summary>
        ///     Registers with the registry and starts the loop. Attaching first is what satisfies the
        ///     producer-attachment deadline and what gives the registry the handle it needs to shut capture down.
        /// </summary>
        /// <exception cref="InvalidOperationException">The session is not live, or admission has closed.</exception>
        internal void Attach()
        {
            // Outside the lock: AttachProducer takes the registry's own gate, and a stop cannot reach this handle
            // in a way that matters until the registration below is published.
            var registration = _owner._registry.AttachProducer(_sessionId, this);

            lock (_gate)
            {
                _detach = registration.Detach;

                // Linked, not independent: EndAsync cancels ProducerToken as its first act, and that is what must
                // stop the capture loop.
                _cancellation = CancellationTokenSource.CreateLinkedTokenSource(registration.ProducerToken);

                // The handle is published into the dictionary BEFORE Attach, so a stop can land in between and find nothing to
                // cancel. Honouring _stopRequested here, and returning BEFORE scheduling, is the only ordering with no window.
                if (_stopRequested)
                {
                    CleanUpLocked();
                    return;
                }

                // Read HERE, not inside the lambda: a lambda that reads `.Token` when the pool thread runs it can
                // find the source already disposed.
                var captureToken = _cancellation.Token;
                Capture = Task.Run(() => RunAsync(captureToken), CancellationToken.None);
            }
        }

        /// <inheritdoc />
        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            // Synchronous by construction, and deliberately NOT awaiting the capture task: a PushAudioAsync blocked behind
            // inference would hold the registry's bounded producer-stop wait, whose admission is already closed. ValueTask only because ILiveAudioProducer says so.
            StopCore();
            return ValueTask.CompletedTask;
        }

        private void StopCore()
        {
            lock (_gate)
            {
                _stopRequested = true;
                CleanUpLocked();
            }
        }

        /// <summary>Cancels, detaches and disposes exactly once, in that order, under <see cref="_gate" />.</summary>
        /// <remarks>
        ///     Cancelling always precedes disposing, and one caller owns both, so no stack can ever see a
        ///     half-torn-down handle. Called only from a stop, or from <see cref="Attach" /> when a stop already asked.
        /// </remarks>
        private void CleanUpLocked()
        {
            if (_cleaned)
            {
                // Idempotent by contract: the registry stops a producer exactly once, but a loop that ended on its
                // own calls this too.
                return;
            }

            if (_cancellation is null)
            {
                // Nothing is published yet. Drop the entry so nothing else finds this handle, and leave the
                // teardown to Attach, which will see _stopRequested under this same lock.
                _ = _owner._captures.TryRemove(new KeyValuePair<Guid, SessionCapture>(_sessionId, this));
                return;
            }

            _cleaned = true;
            try
            {
                // Forced sync: CleanUpLocked runs under lock (_gate) and no await may cross a lock; the cancel must
                // also be observed before _detach is disposed on this same thread.
#pragma warning disable MA0045 // forced sync: called under a lock (see comment above)
                _cancellation.Cancel();
#pragma warning restore MA0045
                _detach?.Dispose();
            }
#pragma warning disable CA1031 // A producer that throws during EndAsync would strand the flush; nothing may escape.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                _owner._logger.LogWarning(exception, "Stopping per-application capture for transcription session {SessionId} was not clean.", _sessionId);
            }
            finally
            {
                _ = _owner._captures.TryRemove(new KeyValuePair<Guid, SessionCapture>(_sessionId, this));
                _cancellation.Dispose();
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                // A stop that won the interlock before Attach finished has already cancelled this token. Bail
                // before building a recorder only to tear it straight down again.
                cancellationToken.ThrowIfCancellationRequested();
                await _owner._source.CaptureAsync(_sessionId, _processId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The stop signal, not a failure.
            }
#pragma warning disable CA1031 // The loop is detached: an escaping exception would be an unobserved task fault.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                // The target process exited, or CoreAudio failed. Either way capture is over; the session lives on
                // until something ends it through the registry.
                _owner._logger.LogError(exception, "Per-application capture for transcription session {SessionId} ended unexpectedly.", _sessionId);
            }
            finally
            {
                StopCore();
            }
        }
    }
}
