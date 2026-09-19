namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The lifecycle of a server-side capture: attach first, run on the registry's own token, stop without waiting
///     for inference, and never end the session itself.
/// </summary>
/// <remarks>
///     <para>
///         Both seams are hand-written fakes, which is the mandated order once the real thing is out of reach: there
///         is no repo fake for either, and NSubstitute cannot express "block on a gate the test controls" or "cancel
///         the producer token from inside the push", which is precisely what the overload path does.
///     </para>
///     <para>
///         The registry fake mirrors the real <c>LiveTranscriptionSessionRegistry</c> where it matters: it hands out
///         a real <c>ProducerToken</c>, holds the attached producer, and on overload cancels that token rather than
///         throwing — because that is what <c>BeginEnd</c> does.
///     </para>
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ProcessAudioCaptureCoordinatorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Start_AttachesAsProducerBeforeBuildingTheRecorder()
    {
        // Attaching second would leave a window in which the session has a running recorder it cannot stop, and
        // would race the producer-attachment deadline that reports NeverAttached.
        var log = new ConcurrentQueue<string>();
        await using var harness = Harness.Create(log);
        var sessionId = Guid.NewGuid();

        var outcome = harness.Coordinator.Start(sessionId, processId: 4321);
        await harness.Source.WaitForCaptureAsync(sessionId, Bound);

        AssertEx.Equal(StartProcessCaptureOutcome.Started, outcome, "A live session on a supported host starts capture.");
        AssertEx.Equal("attach,capture", string.Join(",", log), "The producer is attached before the capture loop is started.");
        AssertEx.NotNull(harness.Registry.ProducerFor(sessionId), "The registry holds the handle it needs to stop this capture.");
    }

    [Test]
    public async Task Start_RefusesWhenTheSessionIsNotLive()
    {
        // The live start (S3) has to have run: a recorder with nowhere to push is worse than a typed refusal.
        await using var harness = Harness.Create();
        harness.Registry.Live = false;
        var sessionId = Guid.NewGuid();

        var outcome = harness.Coordinator.Start(sessionId, processId: 4321);

        AssertEx.Equal(StartProcessCaptureOutcome.SessionNotLive, outcome, "A session that is not live refuses capture.");
        AssertEx.Equal(0, harness.Source.CaptureCalls, "Nothing was started.");
        AssertEx.Null(harness.Registry.ProducerFor(sessionId), "Nothing was attached either.");
    }

    [Test]
    public async Task Start_RunsCaptureUntilStopped()
    {
        await using var harness = Harness.Create();
        var sessionId = Guid.NewGuid();

        _ = harness.Coordinator.Start(sessionId, processId: 4321);
        await harness.Source.WaitForCaptureAsync(sessionId, Bound);
        AssertEx.True(harness.Coordinator.IsCapturing(sessionId), "Capture is running while nothing has stopped it.");

        AssertEx.True(await harness.Coordinator.StopAsync(sessionId, CancellationToken.None), "Stopping a running capture reports that there was one.");

        await AssertEx.EventuallyAsync(() => !harness.Coordinator.IsCapturing(sessionId), Bound,
            "Stopping releases the session so a later start is possible.");
        AssertEx.False(await harness.Coordinator.StopAsync(sessionId, CancellationToken.None), "A second stop reports that there was nothing to stop.");
    }

    [Test]
    public async Task StartTwice_ForSameSession_IsRejected()
    {
        // Two recorders on one session would interleave two audio clocks into one lane.
        await using var harness = Harness.Create();
        var sessionId = Guid.NewGuid();

        var first = harness.Coordinator.Start(sessionId, processId: 4321);
        await harness.Source.WaitForCaptureAsync(sessionId, Bound);
        var second = harness.Coordinator.Start(sessionId, processId: 9876);

        AssertEx.Equal(StartProcessCaptureOutcome.Started, first, "The first start runs.");
        AssertEx.Equal(StartProcessCaptureOutcome.AlreadyCapturing, second, "The second start is refused, not queued.");
        AssertEx.Equal(1, harness.Source.CaptureCalls, "Only one capture loop was ever entered.");
    }

    [Test]
    public async Task ProducerTokenCancelled_StopsTheCaptureLoop()
    {
        // The registry's token, not a private one: EndAsync cancels ProducerToken as its first act, and that has to
        // be what stops the recorder.
        await using var harness = Harness.Create();
        var sessionId = Guid.NewGuid();

        _ = harness.Coordinator.Start(sessionId, processId: 4321);
        await harness.Source.WaitForCaptureAsync(sessionId, Bound);

        harness.Registry.CancelProducerToken(sessionId);

        await AssertEx.EventuallyAsync(() => !harness.Coordinator.IsCapturing(sessionId), Bound,
            "Cancelling the registry's producer token ends the capture loop.");
        AssertEx.True(harness.Registry.WasDetached(sessionId), "The loop detached itself on the way out.");
    }

    [Test]
    public async Task StopAsync_IsIdempotentAndNeverThrows()
    {
        // The registry stops a producer exactly once, but a loop that ended on its own calls this too. A producer
        // that threw during EndAsync would strand the flush.
        await using var harness = Harness.Create();
        var sessionId = Guid.NewGuid();

        _ = harness.Coordinator.Start(sessionId, processId: 4321);
        await harness.Source.WaitForCaptureAsync(sessionId, Bound);
        var producer = AssertEx.NotNull(harness.Registry.ProducerFor(sessionId), "The producer is attached.");

        await producer.StopAsync(CancellationToken.None);
        await producer.StopAsync(CancellationToken.None);
        await producer.StopAsync(CancellationToken.None);

        AssertEx.False(harness.Coordinator.IsCapturing(sessionId), "The capture is gone after the first stop.");
        AssertEx.Equal(1, harness.Registry.DetachCount(sessionId), "Detaching happens exactly once, however often stop is called.");
    }

    [Test]
    public async Task StopAsync_ReturnsWhileAPushIsBlockedBehindInference()
    {
        // This is the test that fails if StopAsync ever awaits the in-flight push. The registry's producer-stop
        // wait is bounded; a producer that waited for a transcription would blow through it and be abandoned.
        await using var harness = Harness.Create();
        var sessionId = Guid.NewGuid();
        harness.Source.FramesToPush = 1;
        using var inference = new ManualGate();
        harness.Registry.PushGate = inference;

        _ = harness.Coordinator.Start(sessionId, processId: 4321);
        await AssertEx.EventuallyAsync(() => harness.Registry.PushesEntered(sessionId) == 1, Bound,
            "The capture loop reached the push and is blocked inside it.");

        var producer = AssertEx.NotNull(harness.Registry.ProducerFor(sessionId), "The producer is attached.");

        await AssertEx.CompletesAsync(producer.StopAsync(CancellationToken.None).AsTask(), Bound,
            "StopAsync returned while a push was still blocked, WITHOUT the gate being released.");

        AssertEx.False(inference.Released, "The proof only counts if the blocked push was never released first.");
        inference.Release();
    }

    [Test]
    public async Task PendingAudioOverload_StopsTheProducerAndDoesNotRetry()
    {
        // The real registry does not throw on overflow: it calls BeginEnd(Overloaded), which cancels ProducerToken
        // before anything else. The fake mirrors that, so this asserts the behaviour the product actually meets.
        await using var harness = Harness.Create();
        var sessionId = Guid.NewGuid();
        harness.Source.FramesToPush = 10;
        harness.Registry.CancelProducerOnPush = true;

        _ = harness.Coordinator.Start(sessionId, processId: 4321);

        await AssertEx.EventuallyAsync(() => !harness.Coordinator.IsCapturing(sessionId), Bound,
            "Overload cancels the producer token, which stops the capture.");
        AssertEx.Equal(1, harness.Registry.PushesEntered(sessionId),
            "The pump stops on the overload rather than retrying into a session that is already ending.");
    }

    [Test]
    public async Task Dispose_DetachesAndStopsEveryActiveCapture()
    {
        var harness = Harness.Create();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        _ = harness.Coordinator.Start(first, processId: 11);
        _ = harness.Coordinator.Start(second, processId: 22);
        await harness.Source.WaitForCaptureAsync(first, Bound);
        await harness.Source.WaitForCaptureAsync(second, Bound);

        await harness.DisposeAsync();

        AssertEx.False(harness.Coordinator.IsCapturing(first), "The first capture is gone.");
        AssertEx.False(harness.Coordinator.IsCapturing(second), "The second capture is gone.");
        AssertEx.Equal(1, harness.Registry.DetachCount(first), "The first producer detached exactly once.");
        AssertEx.Equal(1, harness.Registry.DetachCount(second), "The second producer detached exactly once.");
    }

    [Test]
    public async Task Start_OnAnUnsupportedHost_ReportsNotSupported()
    {
        // The endpoint turns this into a 400; this is the arm that produces it.
        await using var harness = Harness.Create();
        harness.Source.IsSupported = false;
        var sessionId = Guid.NewGuid();

        var outcome = harness.Coordinator.Start(sessionId, processId: 4321);

        AssertEx.Equal(StartProcessCaptureOutcome.NotSupported, outcome, "A host without process loopback refuses before it attaches.");
        AssertEx.Null(harness.Registry.ProducerFor(sessionId), "Nothing was attached.");
        AssertEx.Equal(0, harness.Source.CaptureCalls, "Nothing was started.");
    }

    [Test]
    public async Task StopBetweenPublishAndAttach_LeavesNothingCapturing()
    {
        // The handle is published into the coordinator's dictionary BEFORE it attaches, so a stop can win the
        // interlock while there is still nothing to cancel or detach. Without the re-check at the end of Attach
        // the capture would run untracked from then on: absent from the dictionary, unreachable by any later
        // stop, by IsCapturing and by DisposeAsync, and still pushing PCM into a session whose operator was told
        // capture had stopped.
        await using var harness = Harness.Create();
        var sessionId = Guid.NewGuid();
        var stopWon = false;

        // Driven from inside AttachProducer rather than from a second thread: the interleaving is then exact and
        // the test carries no timing at all. StopAsync completes synchronously here, because at this point the
        // handle has nothing yet to cancel or detach — which is the whole of the race.
        harness.Registry.OnAttaching = () =>
            stopWon = harness.Coordinator.StopAsync(sessionId, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var outcome = harness.Coordinator.Start(sessionId, processId: 4321);

        AssertEx.True(stopWon, "The stop found the published handle, which is exactly how the race arises.");
        AssertEx.Equal(StartProcessCaptureOutcome.Started, outcome, "The start still reports what it did; the re-check is what undoes it.");

        // All three flip without the re-check: the handle stays attached, the registry keeps holding a producer
        // nothing can reach, and the capture runs on with no entry in the coordinator's dictionary.
        AssertEx.False(harness.Coordinator.IsCapturing(sessionId), "Nothing is left capturing.");
        AssertEx.Null(harness.Registry.ProducerFor(sessionId), "The producer was detached, so the registry is not holding a stopped one.");
        AssertEx.Equal(1, harness.Registry.DetachCount(sessionId), "Detaching happened exactly once, on the way out of Attach.");

        // Settle first: a version that scheduled the loop before honouring the stop would have had every chance to
        // run it by now, so this reads as red rather than as a lucky scheduling.
        await AssertEx.SettleAsync();
        AssertEx.Equal(0, harness.Source.CaptureCalls,
            "No recorder was built for a capture that was already stopped. Deterministic, not scheduler-dependent: Attach returns without scheduling the loop at all once a stop has been recorded.");
    }

    [Test]
    public async Task ConcurrentStartAndStop_NeverMisreportTheOutcomeAndAlwaysCleanUpExactlyOnce()
    {
        // Attach publishes the detach handle, then the linked source, then reads its token. A stop running
        // concurrently used to be able to slot its teardown between any two of those: it skipped the cancel
        // because the source was still null, then disposed — from its own finally — the source Attach had just
        // published. Attach's next read of `.Token` threw ObjectDisposedException, which derives from
        // InvalidOperationException, so Start caught it and answered SessionNotLive for a capture that had in
        // fact started and was now running on a token nothing could cancel.
        //
        // The interleaving is a thread race, so this asserts the INVARIANTS that must hold whichever way it goes,
        // over enough attempts to hit the window. It is not a timing test: no assertion waits on the clock.
        const int Attempts = 400;

        await using var harness = Harness.Create();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var sessionId = Guid.NewGuid();

            var start = Task.Run(() => harness.Coordinator.Start(sessionId, processId: 4321));
            var stop = Task.Run(async () => await harness.Coordinator.StopAsync(sessionId, CancellationToken.None));

            var outcome = await start.WaitAsync(Bound);
            _ = await stop.WaitAsync(Bound);

            AssertEx.Equal(StartProcessCaptureOutcome.Started, outcome,
                $"Attempt {attempt}: the session is live and the host is supported, so the only honest answer is Started. SessionNotLive here is the ObjectDisposedException being swallowed as InvalidOperationException.");

            // Whoever lost the race, a final stop settles it; the invariants below must then hold every time.
            _ = await harness.Coordinator.StopAsync(sessionId, CancellationToken.None);

            AssertEx.False(harness.Coordinator.IsCapturing(sessionId), $"Attempt {attempt}: nothing is left capturing.");
            AssertEx.Null(harness.Registry.ProducerFor(sessionId), $"Attempt {attempt}: the registry is not left holding a stopped producer.");
            AssertEx.Equal(1, harness.Registry.DetachCount(sessionId), $"Attempt {attempt}: the handle detached exactly once, never twice and never not at all.");
        }
    }

    /// <summary>The coordinator wired to the two fakes, disposed together.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            ProcessAudioCaptureCoordinator coordinator,
            FakeProcessAudioCaptureSource source,
            RecordingLiveSessionRegistry registry)
        {
            Coordinator = coordinator;
            Source = source;
            Registry = registry;
        }

        public ProcessAudioCaptureCoordinator Coordinator { get; }

        public FakeProcessAudioCaptureSource Source { get; }

        public RecordingLiveSessionRegistry Registry { get; }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "Ownership transfers to the returned harness, whose DisposeAsync disposes both the coordinator and the registry; the caller holds it in an await using.")]
        public static Harness Create(ConcurrentQueue<string>? log = null)
        {
            var registry = new RecordingLiveSessionRegistry(log);
            var source = new FakeProcessAudioCaptureSource(registry, log);
            var coordinator = new ProcessAudioCaptureCoordinator(source,
                registry,
                TimeProvider.System,
                NullLogger<ProcessAudioCaptureCoordinator>.Instance);
            return new Harness(coordinator, source, registry);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            Registry.Dispose();
        }
    }

    /// <summary>A gate the test opens by hand, so nothing in this file waits on the clock.</summary>
    private sealed class ManualGate : IDisposable
    {
        private readonly TaskCompletionSource _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Released { get; private set; }

        public Task Task => _source.Task;

        public void Release()
        {
            Released = true;
            _ = _source.TrySetResult();
        }

        public void Dispose() =>
            Release();
    }

    /// <summary>
    ///     The registry seam: real producer tokens, a recorded attachment, and a push that can block or cancel the
    ///     producer token exactly as the overflow path does.
    /// </summary>
    private sealed class RecordingLiveSessionRegistry : ILiveTranscriptionSessionRegistry, IDisposable
    {
        private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();
        private readonly ConcurrentQueue<string>? _log;

        public RecordingLiveSessionRegistry(ConcurrentQueue<string>? log)
        {
            _log = log;
        }

        public bool Live { get; set; } = true;

        public ManualGate? PushGate { get; set; }

        /// <summary>Invoked inside <see cref="AttachProducer" />, before the producer is stored.</summary>
        public Action? OnAttaching { get; set; }

        public bool CancelProducerOnPush { get; set; }

        public ILiveAudioProducer? ProducerFor(Guid sessionId) =>
            _sessions.TryGetValue(sessionId, out var state) ? state.Producer : null;

        public int DetachCount(Guid sessionId) =>
            _sessions.TryGetValue(sessionId, out var state) ? state.Detaches : 0;

        public bool WasDetached(Guid sessionId) =>
            DetachCount(sessionId) > 0;

        public int PushesEntered(Guid sessionId) =>
            _sessions.TryGetValue(sessionId, out var state) ? state.PushesEntered : 0;

        public void CancelProducerToken(Guid sessionId) =>
            _sessions[sessionId].Cancellation.Cancel();

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "The detach handle is the caller's, exactly as in the real registry: a producer disposes it when it stops on its own.")]
        public LiveProducerRegistration AttachProducer(Guid sessionId, ILiveAudioProducer producer)
        {
            if (!Live)
            {
                throw new InvalidOperationException($"Transcription session {sessionId} is not live.");
            }

            var state = _sessions.GetOrAdd(sessionId, static _ => new SessionState());
            OnAttaching?.Invoke();
            state.Producer = producer;
            _log?.Enqueue("attach");
            return new LiveProducerRegistration { ProducerToken = state.Cancellation.Token, Detach = new Detach(state) };
        }

        public async Task PushAudioAsync(Guid sessionId, TranscriptChannel channel, ReadOnlyMemory<byte> pcm16, CancellationToken cancellationToken)
        {
            var state = _sessions.GetOrAdd(sessionId, static _ => new SessionState());
            _ = Interlocked.Increment(ref state.PushCount);
            state.Pushed.Enqueue((channel, pcm16.ToArray()));

            if (CancelProducerOnPush)
            {
                // Exactly what the real registry's overflow path does: cancel the producer token first, then end.
                // It does NOT throw, so a pump that "handles the overload error" would never see one.
                await state.Cancellation.CancelAsync();
                return;
            }

            if (PushGate is not null)
            {
                // Deliberately NOT cancellable. This models a push sitting behind an inference that does not
                // observe the producer token; a gate that unblocked on cancellation would let a StopAsync which
                // awaits the capture task finish anyway, and the proof would be vacuous.
                await PushGate.Task;
            }
        }

        public bool IsLive(Guid sessionId) =>
            Live;

        public Task StartLiveSessionAsync(Guid sessionId, LiveSessionOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The capture coordinator never starts a live session.");

        public Task EndAsync(Guid sessionId, LiveEndReason reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException("S5 never ends a live session; that is the registry's single termination path.");

        public void NoteBrowserAttached(Guid sessionId, string connectionId) =>
            throw new NotSupportedException("A native capture is not a browser connection.");

        public void NoteBrowserDetached(Guid sessionId, string connectionId) =>
            throw new NotSupportedException("A native capture is not a browser connection.");

        public void Dispose()
        {
            foreach (var state in _sessions.Values)
            {
                state.Cancellation.Dispose();
            }
        }

        private sealed class SessionState
        {
            public int PushCount;

            public CancellationTokenSource Cancellation { get; } = new();

            public ILiveAudioProducer? Producer { get; set; }

            public ConcurrentQueue<(TranscriptChannel Channel, byte[] Pcm)> Pushed { get; } = new();

            public int Detaches { get; set; }

            public int PushesEntered => Volatile.Read(ref PushCount);
        }

        private sealed class Detach : IDisposable
        {
            private readonly SessionState _state;

            public Detach(SessionState state)
            {
                _state = state;
            }

            public void Dispose()
            {
                _state.Detaches++;

                // The real ProducerDetach nulls the session's producer; mirroring it is what lets a test tell
                // "detached" apart from "still registered".
                _state.Producer = null;
            }
        }
    }

    /// <summary>
    ///     A capture source that runs until its token is cancelled, recording how often it was entered and pushing a
    ///     controllable number of frames first.
    /// </summary>
    private sealed class FakeProcessAudioCaptureSource : IProcessAudioCaptureSource
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _entered = new();
        private readonly ILiveTranscriptionSessionRegistry _registry;
        private readonly ConcurrentQueue<string>? _log;
        private int _captureCalls;

        public FakeProcessAudioCaptureSource(ILiveTranscriptionSessionRegistry registry, ConcurrentQueue<string>? log)
        {
            _registry = registry;
            _log = log;
        }

        public bool IsSupported { get; set; } = true;

        public int FramesToPush { get; set; }

        public int CaptureCalls => Volatile.Read(ref _captureCalls);

        public ValueTask<IReadOnlyList<ProcessAudioCaptureCandidate>> ListCandidatesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ProcessAudioCaptureCandidate>>([]);

        public async Task CaptureAsync(Guid sessionId, int processId, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _captureCalls);
            _log?.Enqueue("capture");
            _ = Entered(sessionId).TrySetResult();

            for (var frame = 0; frame < FramesToPush && !cancellationToken.IsCancellationRequested; frame++)
            {
                await _registry.PushAudioAsync(sessionId, TranscriptChannel.Others, new byte[320], cancellationToken);
            }

            // Runs until stopped — a registration on the token rather than any kind of timer, so nothing here waits
            // on the clock.
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => stopped.TrySetResult()))
            {
                await stopped.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task WaitForCaptureAsync(Guid sessionId, TimeSpan timeout) =>
            Entered(sessionId).Task.WaitAsync(timeout);


        private TaskCompletionSource Entered(Guid sessionId) =>
            _entered.GetOrAdd(sessionId, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
