namespace XE_Local_AI_Engine.Tests.Transcription;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Client.Services.Transcription.Live;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The live session registry: its commit pipeline, its one termination path, and the two independent liveness
///     signals a session ends on.
/// </summary>
/// <remarks>
///     Nothing here goes near SignalR. Every test drives <see cref="ILiveTranscriptionSessionRegistry" /> directly,
///     which is also the contract an in-host capture source depends on: a registry that could only be driven through
///     a hub would have failed that requirement before the capture slice started.
///     <para>
///         Every timer is <see cref="ManualTimeProvider" />, every blocked inference is a gate the test releases, and
///         every asynchronous effect is awaited through <see cref="AssertEx.EventuallyAsync" /> — there is no sleep in
///         this file, because the frame queue makes a push's effect arrive after the push returns.
///     </para>
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class LiveTranscriptionSessionRegistryTests
{
    private const string ModelId = "ggml-base";
    private const int GraceSeconds = 5;
    private static readonly TimeSpan AttachmentDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProducerStopBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GracefulFinalizationBound = TimeSpan.FromSeconds(30);

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ProcessCapture_UnexpectedEnd_FailsAndPublishesToTheWatchingBrowser(int outcome)
    {
        await using var fixture = new RegistryFixture(new ScriptedWhisperTranscriber(OneSegment));
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var source = Substitute.For<IProcessAudioCaptureSource>();
        _ = source.IsSupported.Returns(true);
        _ = source.CaptureAsync(sessionId, 4321, Arg.Any<CancellationToken>()).Returns(_ => outcome switch
        {
            0 => Task.CompletedTask,
            1 => Task.FromException(new IOException("Recorder unavailable")),
            _ => Task.FromException(new OperationCanceledException("Recorder cancelled without a stop request"))
        });
        await using var coordinator = new ProcessAudioCaptureCoordinator(source, fixture.Registry, fixture.Time,
            NullLogger<ProcessAudioCaptureCoordinator>.Instance);
        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        fixture.Registry.NoteBrowserAttached(sessionId, "watching-browser");

        AssertEx.Equal(StartProcessCaptureOutcome.Started, coordinator.Start(sessionId, 4321),
            "Admission succeeds before the detached recorder completes or fails.");
        await AssertEx.EventuallyAsync(() => Snapshot(statuses).Count == 1, TimeSpan.FromSeconds(5),
            "A watching browser receives the failure without disconnecting or waiting for abandonment.");

        AssertEx.Equal(LiveEndReason.Failed, Snapshot(statuses)[0], "An unexpected recorder end is a visible failure.");
        AssertEx.False(coordinator.IsCapturing(sessionId), "The failed producer was detached before session teardown.");
        AssertEx.False(fixture.Registry.IsLive(sessionId), "No live session is left without its producer.");
        await fixture.Service.Received(1).CompleteLiveAsync(sessionId, TranscriptionSessionStatus.Failed,
            Arg.Any<long>(), Arg.Any<string?>(), "live-failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessCapture_RequestedCompletion_PreservesTheGracefulFlush()
    {
        await using var fixture = new RegistryFixture(new ScriptedWhisperTranscriber(OneSegment));
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var segments = fixture.RecordSegments();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Substitute.For<IProcessAudioCaptureSource>();
        _ = source.IsSupported.Returns(true);
        _ = source.CaptureAsync(sessionId, 4321, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var cancellationToken = call.ArgAt<CancellationToken>(2);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            _ = entered.TrySetResult();
            await cancelled.Task;
            _ = stopped.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
        });
        await using var coordinator = new ProcessAudioCaptureCoordinator(source, fixture.Registry, fixture.Time,
            NullLogger<ProcessAudioCaptureCoordinator>.Instance);
        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        _ = coordinator.Start(sessionId, 4321);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 500);

        await fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.Equal("Completed", string.Join(',', Snapshot(statuses)), "A requested completion is not changed to failure.");
        AssertEx.Equal(1, Snapshot(segments).Count, "Graceful completion still flushes the final partial audio window.");
        AssertEx.False(coordinator.IsCapturing(sessionId), "The recorder is stopped.");
    }

    [Test]
    public async Task Commit_WhenPersisting_PersistsBeforeItPublishes()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        await WaitForSegmentsAsync(published, count: 1);

        Received.InOrder(() =>
        {
            _ = fixture.Service.AppendLiveSegmentAsync(sessionId,
                seq: 1,
                TranscriptChannel.Mono,
                startMs: 0,
                endMs: 1_000,
                Arg.Any<string>(),
                Arg.Any<double?>(),
                Arg.Any<CancellationToken>());
            _ = fixture.Publisher.PublishSegmentAsync(sessionId,
                seq: 1,
                TranscriptChannel.Mono,
                startMs: 0,
                endMs: 1_000,
                Arg.Any<string>(),
                Arg.Any<double?>(),
                Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Commit_WithPersistFalse_PublishesTheSameEventAndWritesNoRow()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry
                     .StartLiveSessionAsync(sessionId, LiveOptions(persist: false, TranscriptionSourceKind.Dictation), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        var segments = await WaitForSegmentsAsync(published, count: 1);

        AssertEx.Equal("1|Mono|0-1000|w0-1000", Describe(segments[0]),
            "A persist-free session emits the identical commit event; only the row is missing.");
        AssertEx.Empty(fixture.Service.ReceivedCalls(),
            "Nothing about a dictation session may reach the persistence service — the event is the fact, the row is one optional reaction to it.");
    }

    [Test]
    public async Task Seq_WithPersistFalse_StillAdvancesFromTheRegistryCounter()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry
                     .StartLiveSessionAsync(sessionId, LiveOptions(persist: false, TranscriptionSourceKind.Dictation), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        await WaitForSegmentsAsync(published, count: 1);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 1_000, toMs: 2_000);
        var segments = await WaitForSegmentsAsync(published, count: 2);

        AssertEx.Equal("1,2", string.Join(',', segments.Select(static segment => segment.Seq)),
            "The counter is the registry's own, so the client's dedupe-on-sequence works with nothing in the database behind it.");
    }

    [Test]
    public async Task End_WithPersistFalse_DoesNotCallCompleteLive()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        await fixture.Registry
                     .StartLiveSessionAsync(sessionId, LiveOptions(persist: false, TranscriptionSourceKind.Dictation), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 500);
        await fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);

        AssertEx.Empty(fixture.Service.ReceivedCalls(), "There is no row to terminalize, so no status moves.");
        await fixture.Publisher.Received(1).PublishStatusAsync(sessionId, LiveEndReason.Completed, Arg.Any<CancellationToken>());
        AssertEx.False(fixture.Registry.IsLive(sessionId), "The entry is still dropped.");
    }

    [Test]
    public async Task PushAudioAsync_FeedsTheLaneWithoutAnyHubInvolvement()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);

        // No hub, no connection, no caller context: this is the whole seam an in-process capture source uses.
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        var segments = await WaitForSegmentsAsync(published, count: 1);

        AssertEx.Equal("1|Mono|0-1000|w0-1000", Describe(segments[0]), "The lane committed the audio it was handed directly.");
        AssertEx.Equal(expected: 1, transcriber.CallCount, "And it reached the transcriber exactly once.");
    }

    [Test]
    public async Task Seq_IsAssignedInCommitOrderAcrossBothLanes()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(channels: TwoLanes), CancellationToken.None);

        // Serialized on purpose: the assertion is that the sequence follows COMMIT order, which is only a statement
        // about anything if the test decides that order rather than the scheduler.
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.You, fromMs: 0, toMs: 1_000);
        await WaitForSegmentsAsync(published, count: 1);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Others, fromMs: 0, toMs: 1_000);
        var segments = await WaitForSegmentsAsync(published, count: 2);

        AssertEx.Equal("1|You|0-1000|w0-1000;2|Others|0-1000|w0-1000",
            string.Join(';', segments.Select(Describe)),
            "Both lanes draw from one counter, in the order their commits crossed the session lock.");
    }

    [Test]
    public async Task Seq_StartsAtOne()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        var segments = await WaitForSegmentsAsync(published, count: 1);

        AssertEx.Equal(expected: 1L, segments[0].Seq,
            "A row numbered zero is excluded from its own session's first replay, because a fresh subscriber asks for everything after zero.");
    }

    [Test]
    public async Task Seq_ResumesAfterTheRowsASessionAlreadyHolds()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(startingSeq: 7), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        var segments = await WaitForSegmentsAsync(published, count: 1);

        AssertEx.Equal(expected: 8L, segments[0].Seq,
            "The first live commit is one past what the transcript already holds; re-allocating a taken sequence would hit the unique index.");
    }

    [Test]
    public async Task Commit_WithBlockedSeqOnePersistence_DoesNotPublishSeqTwoFirst()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        var firstAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendCount = 0;
        _ = fixture.Service.AppendLiveSegmentAsync(Arg.Any<Guid>(),
                       Arg.Any<long>(),
                       Arg.Any<TranscriptChannel>(),
                       Arg.Any<long>(),
                       Arg.Any<long>(),
                       Arg.Any<string>(),
                       Arg.Any<double?>(),
                       Arg.Any<CancellationToken>())
                   .Returns(_ => Interlocked.Increment(ref appendCount) == 1 ? firstAppend.Task : Task.CompletedTask);

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(channels: TwoLanes), CancellationToken.None);

        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.You, fromMs: 0, toMs: 1_000);
        await AssertEx.EventuallyAsync(() => Volatile.Read(ref appendCount) == 1, TestBudgets.Contended,
            "The first lane must reach persistence before the second one is released.");

        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Others, fromMs: 0, toMs: 1_000);
        await AssertEx.EventuallyAsync(() => transcriber.CallCount == 2, TestBudgets.Contended,
            "The second lane transcribes freely; it is the COMMIT it must queue behind.");
        await AssertEx.SettleAsync();

        AssertEx.Empty(Snapshot(published),
            "Sequence two must not be published while sequence one is still inside its write: the client drops anything at or below its watermark, so the first segment would be lost for good.");

        firstAppend.SetResult();
        var segments = await WaitForSegmentsAsync(published, count: 2);

        AssertEx.Equal("1|You;2|Others", string.Join(';', segments.Select(static segment => $"{segment.Seq}|{segment.Channel}")),
            "Allocation, persistence and publication are one critical section, so publication order follows allocation order.");
    }

    [Test]
    public async Task EndAsync_IsIdempotent_AndRunsStopFlushDrainCompletePublishRemoveInThatOrder()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var order = fixture.RecordOrder();
        var producer = new RecordingAudioProducer(order);

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        producer.Token = fixture.Registry.AttachProducer(sessionId, producer).ProducerToken;
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 500);

        var first = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        var second = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await Task.WhenAll(first, second);
        await fixture.Registry.EndAsync(sessionId, LiveEndReason.Cancelled, CancellationToken.None);

        AssertEx.Equal("stop,append,complete,status", string.Join(',', Snapshot(order)),
            "The producer stops before the lanes flush, the flushed segments persist before the row terminalizes, and the status push is last.");
        AssertEx.Equal(expected: 1, producer.StopCount, "A second call rides the first one's task; the producer is stopped exactly once.");
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Completed,
                         Arg.Any<long>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<CancellationToken>());
        AssertEx.False(fixture.Registry.IsLive(sessionId), "And the entry is gone.");
    }

    [Test]
    public async Task EndAsync_ViaCancelAndDelete_TakesTheSameTerminationPath()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        var cancelled = Guid.NewGuid();
        var deleted = Guid.NewGuid();
        _ = registry.IsRegistered(Arg.Any<Guid>()).Returns(true);

        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);

        _ = await harness.Service.CancelAsync(cancelled, CancellationToken.None);
        _ = await harness.Service.DeleteSessionAsync(deleted, CancellationToken.None);

        await registry.Received(1).EndAsync(cancelled, LiveEndReason.Cancelled, Arg.Any<CancellationToken>());
        await registry.Received(1).EndAsync(deleted, LiveEndReason.Cancelled, Arg.Any<CancellationToken>());
        await registry.DidNotReceive().EndAsync(Arg.Any<Guid>(), LiveEndReason.Completed, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProducerThatNeverAttaches_EndsTheSessionAfterTheDeadline()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);

        AssertEx.Equal(expected: 1, fixture.Time.ArmedTimerCount, "The deadline is armed at REGISTRATION, not at the first frame.");
        AssertEx.True(fixture.Registry.IsLive(sessionId), "Nothing has happened yet.");

        // The denied-microphone case: no frame ever arrives and no connection is ever lost, so nothing else would
        // ever reclaim this session.
        fixture.Time.Advance(AttachmentDeadline);

        await WaitForEndAsync(statuses, LiveEndReason.NeverAttached);
        AssertEx.False(fixture.Registry.IsLive(sessionId), "A session nothing ever feeds must end by itself.");
    }

    [Test]
    public async Task AttachingAProducer_DisarmsTheAttachmentDeadlineWithoutASingleFrame()
    {
        // The positive control for the test above. A native capture of an application that happens to be silent
        // pushes nothing at all — WASAPI never yields a silent packet — so a deadline that only the first FRAME
        // disarmed would reap a session whose recorder was running perfectly well.
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(sourceKind: TranscriptionSourceKind.ApplicationProcess),
            CancellationToken.None);

        var producer = new SilentProducer();
        var registration = fixture.Registry.AttachProducer(sessionId, producer);
        using (registration.Detach)
        {
            fixture.Time.Advance(AttachmentDeadline + TimeSpan.FromSeconds(1));
            await AssertEx.SettleAsync();

            AssertEx.True(fixture.Registry.IsLive(sessionId),
                "Attaching satisfies the deadline; a silent application must not be reaped as NeverAttached.");
            AssertEx.Empty(Snapshot(statuses), "No end of any kind happened.");
            AssertEx.False(producer.Stopped, "Nothing stopped the producer either.");
            AssertEx.False(registration.ProducerToken.IsCancellationRequested, "The producer token is still live.");
        }
    }

    [Test]
    public async Task NativeAudioArrival_DoesNotCancelTheBrowserAbandonmentGrace()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        var statuses = fixture.RecordStatuses();
        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        fixture.Registry.NoteBrowserAttached(sessionId, "connection-1");
        fixture.Registry.NoteBrowserDetached(sessionId, "connection-1");

        // Native capture keeps feeding the session after the tab closed. It must not look like the tab came back.
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);

        fixture.Time.Advance(TimeSpan.FromSeconds(GraceSeconds));

        await WaitForEndAsync(statuses, LiveEndReason.Abandoned);
        AssertEx.False(fixture.Registry.IsLive(sessionId), "A closed tab ends the session whatever else is still producing audio.");
    }

    [Test]
    public async Task BrowserThatDisconnectsBeforeTheFirstFrame_ArmsTheGrace()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        var statuses = fixture.RecordStatuses();
        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);

        // Registration happens on subscribe, so a connection that loses its microphone before pushing anything still
        // arms the grace when it drops.
        fixture.Registry.NoteBrowserAttached(sessionId, "connection-1");
        fixture.Registry.NoteBrowserDetached(sessionId, "connection-1");

        AssertEx.Equal(expected: 2, fixture.Time.ArmedTimerCount, "Both the attachment deadline and the grace are armed.");
        fixture.Time.Advance(TimeSpan.FromSeconds(GraceSeconds));

        // Abandoned, not NeverAttached: the grace is what reclaimed it, and the much longer deadline never elapsed.
        await WaitForEndAsync(statuses, LiveEndReason.Abandoned);
        AssertEx.False(fixture.Registry.IsLive(sessionId));
    }

    [Test]
    public async Task PendingAudioBeyondTheBudget_EndsTheSessionOverloadedAndStopsTheProducer()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var producer = new RecordingAudioProducer();
        var statuses = fixture.RecordStatuses();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        producer.Token = fixture.Registry.AttachProducer(sessionId, producer).ProducerToken;

        // One second of audio per frame, into a transcriber that never answers. The budget is four two-second windows.
        var budgetBytes = 4 * 2 * WavPcm16.SampleRate * 2;
        var frameBytes = 1_000 * WavPcm16.BytesPerMillisecond;

        // The first frame is allowed to reach the transcriber before the rest are pushed. Without this the whole
        // burst can be admitted and refused before inference ever starts, which is a real outcome but not the one
        // this test is about: the finding is a lane held open by a slow transcriber while audio keeps arriving.
        await fixture.Registry
                     .PushAudioAsync(sessionId, TranscriptChannel.Mono, LivePcm.Range(0, 1_000), CancellationToken.None);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);

        var accepted = 1;
        for (var frame = 1; frame < 12 && fixture.Registry.IsLive(sessionId); frame++)
        {
            await fixture.Registry
                         .PushAudioAsync(sessionId, TranscriptChannel.Mono, LivePcm.Range(frame * 1_000, (frame + 1) * 1_000), CancellationToken.None);
            accepted++;
        }

        await WaitForEndAsync(statuses, LiveEndReason.Overloaded);
        AssertEx.Equal(0, fixture.Logger.CountContaining("lane of transcription session"),
            "Cancelled queued frames must not report secondary lane failures.");

        AssertEx.Equal(budgetBytes / frameBytes, accepted - 1,
            $"Exactly the budget was retained before the refusal: {accepted - 1} frames of {frameBytes} bytes against a budget of {budgetBytes}.");
        await AssertEx.EventuallyAsync(() => producer.StopCount == 1, TestBudgets.Contended,
            "The producer is stopped promptly, while the transcriber is still holding the lane.");
        AssertEx.Equal(expected: 1, transcriber.CallCount, "The gate was never released, so nothing drained behind the scenes.");
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Failed,
                         Arg.Any<long>(),
                         Arg.Any<string?>(),
                         "live-overloaded",
                         Arg.Any<string?>(),
                         Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ABurstQueuedBehindASlowInference_DrainsAtOneInferencePerWindow()
    {
        // The first inference is slow (a cold model load), so six 1 s frames queue behind it. Re-inferring at every
        // tick would cost six requests for six seconds and never catch up; behind, the lane submits only at the cap.
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        await fixture.Registry.PushAudioAsync(sessionId, TranscriptChannel.Mono, LivePcm.Range(0, 1_000), CancellationToken.None);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);
        for (var frame = 1; frame < 6; frame++)
        {
            await fixture.Registry.PushAudioAsync(sessionId, TranscriptChannel.Mono, LivePcm.Range(frame * 1_000, (frame + 1) * 1_000), CancellationToken.None);
        }

        transcriber.Release();

        // [0,1] is the parked tick; [1,3] and [3,5] are cap submissions while behind; [5,6] is the ordinary tick of
        // the last frame, which has nothing queued behind it.
        var segments = await WaitForSegmentsAsync(published, count: 4);
        AssertEx.Equal("0-1000;1000-3000;3000-5000;5000-6000",
            string.Join(';', segments.Select(segment => $"{segment.StartMs}-{segment.EndMs}")),
            "Every millisecond is transcribed once, in cap-sized windows while the lane was behind.");
        AssertEx.Equal(4, transcriber.CallCount, "Six queued frames cost four inferences, not six.");
        AssertEx.True(fixture.Registry.IsLive(sessionId), "Catching up is not an overload.");
    }

    [Test]
    public async Task EndAsync_DuringInference_ReturnsPromptly()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);

        // The gate is never released. Ending must not be a second thing waiting on the same answer.
        await AssertEx.CompletesAsync(fixture.Registry.EndAsync(sessionId, LiveEndReason.Cancelled, CancellationToken.None),
            TestBudgets.Contended,
            "Ending a session waited behind the transcriber instead of cancelling it.");

        AssertEx.Equal(expected: 1, transcriber.CallCount, "And it did not re-submit the retained audio on the way out.");
        AssertEx.False(fixture.Registry.IsLive(sessionId));
    }

    [Test]
    public async Task EndAsync_ClosesAudioAdmissionBeforeCancellingTheProducerAndFlushing()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var order = fixture.RecordOrder();
        var producer = new RecordingAudioProducer(order)
        {
            Hangs = true
        };

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        producer.Token = fixture.Registry.AttachProducer(sessionId, producer).ProducerToken;
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 500);

        var ending = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await producer.Entered.WaitAsync(TestBudgets.Contended);

        // Issued between "stop the producer" and "flush the lanes": admission is already shut, so this must not reach
        // a lane, be transcribed, or appear in the flushed transcript.
        AssertEx.False(fixture.Registry.IsLive(sessionId), "Admission closes first, synchronously.");
        await fixture.Registry
                     .PushAudioAsync(sessionId, TranscriptChannel.Mono, LivePcm.Range(5_000, 6_000), CancellationToken.None);

        producer.Release();
        await ending;

        AssertEx.Equal("stop,append,complete,status", string.Join(',', Snapshot(order)), "The fixed order holds.");
        AssertEx.Empty(transcriber.Windows.Where(static window => window.StartMs >= 5_000),
            "A frame issued after admission closed reached a lane.");
    }

    [Test]
    public async Task GracefulStop_AllowsBacklogBeyondFiveSecondsAndPreservesItsTail()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var id = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var segments = fixture.RecordSegments();
        await fixture.Registry.StartLiveSessionAsync(id, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, id, TranscriptChannel.Mono, 0, 1_500);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);
        var ending = fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => fixture.Time.ArmedTimerCount == 1, TestBudgets.Contended);
        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        AssertEx.False(ending.IsCompleted, "A healthy backlog is not abandoned at the old five-second bound.");
        transcriber.Release();
        await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "Releasing the backlog must allow finalization.");
        AssertEx.Equal("Completed", string.Join(',', Snapshot(statuses)));
        AssertEx.Equal(1_500L, Snapshot(segments)[^1].EndMs, "The sub-tick tail is committed by the final flush.");
        AssertEx.False(fixture.Registry.IsRegistered(id));
    }

    [Test]
    public async Task GracefulStop_TwoBlockedLanesShareTheSameDeadline()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var id = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        await fixture.Registry.StartLiveSessionAsync(id, LiveOptions(channels: TwoLanes), CancellationToken.None);
        await PushAsync(fixture.Registry, id, TranscriptChannel.You, 0, 1_000);
        await PushAsync(fixture.Registry, id, TranscriptChannel.Others, 0, 1_000);
        await AssertEx.EventuallyAsync(() => transcriber.CallCount == 2, TestBudgets.Contended);
        var ending = fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => fixture.Time.ArmedTimerCount == 1, TestBudgets.Contended);
        fixture.Time.Advance(GracefulFinalizationBound);
        await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "Two lanes do not receive two consecutive thirty-second budgets.");
        AssertEx.Equal("Failed", string.Join(',', Snapshot(statuses)));
        AssertEx.Equal(2, transcriber.CallCount, "Neither blocked lane is flushed concurrently with its pending push.");
    }

    [Test]
    public async Task GracefulStop_DrainAndFlushShareOneThirtySecondDeadline()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcriber = Substitute.For<IWhisperTranscriber>();
        var scripted = new ScriptedWhisperTranscriber(OneSegment);
        var calls = 0;
        _ = transcriber.TranscribeAsync(Arg.Any<string>(), Arg.Any<WhisperTranscriptionRequest>(), Arg.Any<CancellationToken>())
                       .Returns(async call =>
                       {
                           var token = call.ArgAt<CancellationToken>(2);
                           if (Interlocked.Increment(ref calls) == 1)
                           {
                               _ = first.TrySetResult();
                               await release.Task.WaitAsync(token);
                           }
                           else
                           {
                               _ = second.TrySetResult();
                               await Task.Delay(Timeout.InfiniteTimeSpan, token);
                           }

                           return await scripted.TranscribeAsync(call.ArgAt<string>(0), call.ArgAt<WhisperTranscriptionRequest>(1), token);
                       });
        await using var fixture = new RegistryFixture(transcriber);
        var id = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        await fixture.Registry.StartLiveSessionAsync(id, LiveOptions(), CancellationToken.None);
        // The zero-guard first tick commits its whole window. A queued half-tick must remain for the final flush.
        await PushAsync(fixture.Registry, id, TranscriptChannel.Mono, 0, 1_500);
        await first.Task.WaitAsync(TestBudgets.Contended);
        var ending = fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => fixture.Time.ArmedTimerCount == 1, TestBudgets.Contended);
        fixture.Time.Advance(TimeSpan.FromSeconds(20));
        _ = release.TrySetResult();
        await second.Task.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(2, Volatile.Read(ref calls), "The second inference is the final flush of the remaining half-tick.");
        AssertEx.Equal("[0,1000)", string.Join(';', scripted.Windows));
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        AssertEx.False(ending.IsCompleted, "The flush still has one second of the shared budget.");
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "The flush cannot start a fresh thirty-second budget.");
        AssertEx.Equal("Failed", string.Join(',', Snapshot(statuses)));
        await fixture.Service.Received(1).CompleteLiveAsync(id, TranscriptionSessionStatus.Failed, Arg.Any<long>(),
            Arg.Any<string?>(), "live-flush-failed", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CancelOrShutdown_InterruptsGracefulDrainOrFlushAndRejectsLateResults(bool flush, bool shutdown)
    {
        var transcriber = new GatedWhisperTranscriber(static _ =>
        [
            new WhisperTranscriptSegment
            {
                StartSeconds = 0,
                EndSeconds = 0.1,
                Text = "late",
                Confidence = 0.9
            }
        ])
        {
            IgnoresCancellation = true
        };
        await using var fixture = new RegistryFixture(transcriber);
        var id = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var segments = fixture.RecordSegments();
        await fixture.Registry.StartLiveSessionAsync(id, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, id, TranscriptChannel.Mono, 0, flush ? 500 : 1_000);
        var ending = fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);
        AssertEx.True(fixture.Registry.IsRegistered(id));
        AssertEx.False(fixture.Registry.IsLive(id));
        try
        {
            if (shutdown)
            {
                await AssertEx.CompletesAsync(fixture.Registry.DisposeAsync().AsTask(), TestBudgets.Contended,
                    "Shutdown must interrupt finalization without waiting for the runtime to cooperate.");
            }
            else
            {
                await using var service = await TranscriptionServiceHarness.CreateAsync(fixture.Registry);
                var cancelling = service.Service.CancelAsync(id, CancellationToken.None);
                await AssertEx.CompletesAsync(cancelling, TestBudgets.Contended, "REST cancellation interrupts the graceful wait.");
                AssertEx.True(await cancelling,
                    "A finalizing session is still owned, so the existing cancel endpoint must acknowledge it.");
                AssertEx.False(await service.Service.CancelAsync(id, CancellationToken.None), "An ended session is no longer registered.");
            }

            await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "The original Stop caller joins the escalated end.");
            AssertEx.Equal("Cancelled", string.Join(',', Snapshot(statuses)));
            await fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None);
            AssertEx.Equal(1, Snapshot(statuses).Count, "Replayed Stop must not publish another outcome.");
        }
        finally
        {
            transcriber.Release();
        }

        await AssertEx.EventuallyAsync(() => fixture.Logger.CountContaining("Dropping a") > 0 || Snapshot(segments).Count > 0,
            TestBudgets.Contended, "The late result must reach the commit guard, not silently disappear before it.");
        AssertEx.Empty(Snapshot(segments));
        await fixture.Service.DidNotReceive().AppendLiveSegmentAsync(Arg.Any<Guid>(), Arg.Any<long>(),
            Arg.Any<TranscriptChannel>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<double?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelAfterOutcomeFreeze_JoinsWithoutRewritingCompleted()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var id = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var completing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = fixture.Service.CompleteLiveAsync(id, TranscriptionSessionStatus.Completed, Arg.Any<long>(),
                       Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                   .Returns(async call =>
                   {
                       _ = call;
                       _ = completing.TrySetResult();
                       await release.Task;
                   });
        await fixture.Registry.StartLiveSessionAsync(id, LiveOptions(), CancellationToken.None);
        var stopping = fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None);
        await completing.Task.WaitAsync(TestBudgets.Contended);
        var cancelling = fixture.Registry.EndAsync(id, LiveEndReason.Cancelled, CancellationToken.None);
        try
        {
            AssertEx.False(cancelling.IsCompleted, "Cancellation joins the terminal write already in progress.");
        }
        finally
        {
            _ = release.TrySetResult();
        }

        await AssertEx.CompletesAsync(Task.WhenAll(stopping, cancelling), TestBudgets.Contended, "The frozen outcome is published once.");
        AssertEx.Equal("Completed", string.Join(',', Snapshot(statuses)));
        await fixture.Service.Received(1).CompleteLiveAsync(id, TranscriptionSessionStatus.Completed, Arg.Any<long>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GracefulFlushThatIgnoresCancellation_TimesOutAndRejectsItsLateCommit()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment)
        {
            IgnoresCancellation = true
        };
        await using var fixture = new RegistryFixture(transcriber);
        var id = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var segments = fixture.RecordSegments();
        await fixture.Registry.StartLiveSessionAsync(id, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, id, TranscriptChannel.Mono, 0, 500);
        var ending = fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);
        fixture.Time.Advance(GracefulFinalizationBound);
        try
        {
            await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "A non-cooperative final inference must not extend the deadline.");
            AssertEx.Equal("Failed", string.Join(',', Snapshot(statuses)));
        }
        finally
        {
            transcriber.Release();
        }

        await AssertEx.EventuallyAsync(() => fixture.Logger.CountContaining("Dropping a") > 0 || Snapshot(segments).Count > 0,
            TestBudgets.Contended);
        AssertEx.Empty(Snapshot(segments));
    }

    [Test]
    public async Task EndAsync_WithALaneThatWillNotDrain_SkipsItsFlushAndFinalizesFailed()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);

        // A graceful end does NOT cancel the in-flight submission, so this lane is still inside PushAsync when the
        // drain bound runs out. The segmenter is single-threaded by contract: flushing it now would run a second
        // call against the buffer the first one is still rewriting.
        var ending = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => fixture.Time.ArmedTimerCount == 1, TestBudgets.Contended,
            "The drain bound must be armed before the clock is moved past it.");

        fixture.Time.Advance(GracefulFinalizationBound);

        // Failed, not Completed: the operator's last window never reached the model and the retained audio is gone,
        // so reporting success would hand them a whole-looking transcript that is missing its final seconds.
        await WaitForEndAsync(statuses, LiveEndReason.Failed);
        await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "A lane that will not drain must be abandoned, never allowed to hold the session open.");
        AssertEx.Equal(expected: 1, transcriber.CallCount,
            "The undrained lane was flushed anyway, which is a second concurrent call into a segmenter that is not thread-safe.");
        AssertEx.Empty(Snapshot(statuses).Where(static status => status == LiveEndReason.Completed),
            "Completed must never be published for an end that could not finalize.");
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Failed,
                         Arg.Any<long>(),
                         Arg.Any<string?>(),
                         "live-flush-failed",
                         Arg.Any<string?>(),
                         Arg.Any<CancellationToken>());
        AssertEx.False(fixture.Registry.IsLive(sessionId));

        transcriber.Release();
    }

    [Test]
    public async Task PushAudioAsync_CopiesTheFrame_SoAProducerMayReuseItsBuffer()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);

        // The first frame occupies the lane, so the second one is still sitting in the queue — unread — when the
        // producer gets control back and recycles its capture buffer.
        await fixture.Registry.PushAudioAsync(sessionId, TranscriptChannel.Mono, LivePcm.Range(0, 1_000), CancellationToken.None);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);

        var reused = LivePcm.Range(1_000, 2_000).ToArray();
        await fixture.Registry.PushAudioAsync(sessionId, TranscriptChannel.Mono, reused, CancellationToken.None);
        LivePcm.Range(9_000, 10_000).Span.CopyTo(reused);

        transcriber.Release();

        // Waited on the second COMMIT, not on the fake's call count: the count is incremented on entry, so the
        // window list can still be one short when it reaches two and the assertion below would read a half-written
        // list and fail for the wrong reason.
        await WaitForSegmentsAsync(published, count: 2);

        AssertEx.Equal("[0,1000);[1000,2000)", string.Join(';', transcriber.Windows),
            "The queued frame was read out of the caller's buffer, so the transcript received whatever the producer put there next.");
    }

    [Test]
    public async Task LaneThatAnswersAfterFinalization_IsNeitherPersistedNorPublished()
    {
        // Ignores cancellation, so its answer really does arrive after the session has published its terminal
        // status — the case the finalization flag exists for rather than one the abort token already covers.
        // Two segments: one that commits and one that stays provisional, so the late tick carries BOTH a commit and
        // a partial and each has to be refused on its own.
        var transcriber = new GatedWhisperTranscriber(static _ =>
        [
            new WhisperTranscriptSegment
            {
                StartSeconds = 0.0,
                EndSeconds = 0.8,
                Text = "committed",
                Confidence = 0.9
            },
            new WhisperTranscriptSegment
            {
                StartSeconds = 0.8,
                EndSeconds = 1.2,
                Text = "provisional",
                Confidence = 0.9
            }
        ])
        {
            IgnoresCancellation = true
        };
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);

        var ending = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => fixture.Time.ArmedTimerCount == 1, TestBudgets.Contended,
            "The drain bound must be armed before the clock is moved past it.");
        fixture.Time.Advance(GracefulFinalizationBound);
        await WaitForEndAsync(statuses, LiveEndReason.Failed);
        await ending;

        // Now let the abandoned inference answer. Its commit is real; it is simply too late to exist.
        transcriber.Release();

        // Waited on the registry's OWN handling of that late result — the drop it logs, or the publish it should
        // never make. Settling the scheduler is documented as a heuristic, so a test that only settled would pass
        // with the guard removed whenever the continuation happened to be slow.
        await AssertEx.EventuallyAsync(() => fixture.Logger.CountContaining("Dropping a") > 0 || Snapshot(published).Count > 0,
            TestBudgets.Contended,
            "The abandoned lane's late answer never reached the commit pipeline at all, so this test proved nothing.");

        AssertEx.Empty(Snapshot(published),
            "A commit published after the terminal status would never be replayed, because the client has already stopped listening.");
        AssertEx.Empty(fixture.Service.ReceivedCalls()
                              .Where(static call => string.Equals(call.GetMethodInfo().Name, "AppendLiveSegmentAsync", StringComparison.Ordinal)),
            "And it must not reach the transcript either: the row is already terminal.");
        await fixture.Publisher.DidNotReceive()
                     .PublishPartialAsync(Arg.Any<Guid>(), Arg.Any<TranscriptChannel>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EndAsync_WithOneLaneBlocked_StillFlushesTheHealthyLane()
    {
        // Only the first call parks, and only the You lane submits before the end, so You is the wedged lane and the
        // Others flush is free to answer. One shared gate would have wedged both lanes and proved nothing.
        var transcriber = new GatedWhisperTranscriber(OneSegment)
        {
            GatesFirstCallOnly = true
        };
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(channels: TwoLanes), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.You, fromMs: 0, toMs: 1_000);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Others, fromMs: 0, toMs: 500);

        var ending = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => fixture.Time.ArmedTimerCount == 1, TestBudgets.Contended,
            "The blocked lane's drain bound must be armed before the clock is moved past it.");
        await WaitForSegmentsAsync(published, count: 1);
        fixture.Time.Advance(GracefulFinalizationBound);

        // The blocked lane is abandoned and the session is Failed because of it. The healthy lane is a different
        // lane: cancelling the session-wide token here would throw away speech that was ready to commit.
        await WaitForEndAsync(statuses, LiveEndReason.Failed);
        await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "The end still finishes.");

        AssertEx.Equal("1|Others|0-500|w0-500", string.Join(';', Snapshot(published).Select(Describe)),
            "One lane missing its drain deadline must not take the other lane's flushable speech with it.");
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Failed,
                         Arg.Any<long>(),
                         Arg.Any<string?>(),
                         "live-flush-failed",
                         Arg.Any<string?>(),
                         Arg.Any<CancellationToken>());

        transcriber.Release();
    }

    [Test]
    public async Task EndAsync_WhenTheFlushThrows_FinalizesTheSessionFailed()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var statuses = fixture.RecordStatuses();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);

        // Half a tick, so the retained speech exists and only the flush can turn it into a segment.
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 500);
        transcriber.Failure = new InvalidOperationException("the runtime went away");

        await fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);

        AssertEx.Equal("Failed", string.Join(',', Snapshot(statuses)),
            "A flush that threw leaves the operator without their last words; Completed would report success for an incomplete transcript.");
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Failed,
                         Arg.Any<long>(),
                         Arg.Any<string?>(),
                         "live-flush-failed",
                         "The final window could not be transcribed; the transcript may be missing its last seconds.",
                         Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartLive_ThatLosesTheRegistrationRace_ReportsAlreadyLiveAndLeavesTheWinnersRow()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();

        // The winner registered between this caller's status write and its own registration attempt.
        _ = registry.IsLive(Arg.Any<Guid>()).Returns(true);
        _ = registry.StartLiveSessionAsync(Arg.Any<Guid>(), Arg.Any<LiveSessionOptions>(), Arg.Any<CancellationToken>())
                    .Returns<Task>(call => throw new LiveSessionAlreadyRegisteredException(call.ArgAt<Guid>(0)));

        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);
        var created = await harness.Service
                                   .CreateSessionAsync(new CreateTranscriptionSessionInput
                                   {
                                       SourceKind = "Microphone"
                                   }, CancellationToken.None);

        var result = await harness.Service.StartLiveAsync(created.Id, CancellationToken.None);

        AssertEx.Equal(StartLiveOutcome.AlreadyLive, result.Outcome, "The loser reports what a sequential second call reports.");
        var row = AssertEx.NotNull(await harness.Service.GetSessionAsync(created.Id, CancellationToken.None), "The row still exists.");
        AssertEx.Equal(TranscriptionSessionStatus.Transcribing, row.Status,
            "Rolling back here would reset the WINNER's row to Created while its lanes are live.");
    }

    [Test]
    public async Task StartLive_ThatLosesToAWinnerAlreadyEnding_StillReportsAlreadyLiveAndLeavesTheRow()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();

        // The winner has already begun ending, so admission is closed and IsLive is false — the state the old
        // exception filter consulted, and the one under which it wrongly rolled a finished session back to Created.
        _ = registry.IsLive(Arg.Any<Guid>()).Returns(false);
        _ = registry.StartLiveSessionAsync(Arg.Any<Guid>(), Arg.Any<LiveSessionOptions>(), Arg.Any<CancellationToken>())
                    .Returns<Task>(call => throw new LiveSessionAlreadyRegisteredException(call.ArgAt<Guid>(0)));

        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);
        var created = await harness.Service
                                   .CreateSessionAsync(new CreateTranscriptionSessionInput
                                   {
                                       SourceKind = "Microphone"
                                   }, CancellationToken.None);

        var result = await harness.Service.StartLiveAsync(created.Id, CancellationToken.None);

        AssertEx.Equal(StartLiveOutcome.AlreadyLive, result.Outcome, "Duplicate registration is identified by its own type, never by liveness.");
        var row = AssertEx.NotNull(await harness.Service.GetSessionAsync(created.Id, CancellationToken.None), "The row still exists.");
        AssertEx.Equal(TranscriptionSessionStatus.Transcribing, row.Status,
            "Rolling back here would put a session the winner is finishing back into Created, after its terminal write.");
    }

    [Test]
    public async Task StartLive_WhenTheRegistryIsShuttingDown_RollsTheRowBackAndRethrows()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();

        // ObjectDisposedException derives from InvalidOperationException, so the old filter reported a node that was
        // shutting down as a session that was already live — and left the row in Transcribing behind nothing.
        _ = registry.IsLive(Arg.Any<Guid>()).Returns(true);
        _ = registry.StartLiveSessionAsync(Arg.Any<Guid>(), Arg.Any<LiveSessionOptions>(), Arg.Any<CancellationToken>())
                    .Returns<Task>(_ => throw new ObjectDisposedException(nameof(LiveTranscriptionSessionRegistry)));

        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);
        var created = await harness.Service
                                   .CreateSessionAsync(new CreateTranscriptionSessionInput
                                   {
                                       SourceKind = "Microphone"
                                   }, CancellationToken.None);

        _ = await AssertEx.ThrowsAsync<ObjectDisposedException>(() => harness.Service.StartLiveAsync(created.Id, CancellationToken.None),
            "A registry that is shutting down is a failed start, not a duplicate one.");

        var row = AssertEx.NotNull(await harness.Service.GetSessionAsync(created.Id, CancellationToken.None), "The row still exists.");
        AssertEx.Equal(TranscriptionSessionStatus.Created, row.Status,
            "A row left in Transcribing with no registry entry accepts no audio and never ends.");
    }

    /// <summary>
    ///     A start whose row is finished by another writer between its read and its write must refuse, not resurrect.
    /// </summary>
    /// <remarks>
    ///     The read is held open on purpose. Completing the row BEFORE calling start proves nothing: the existing
    ///     terminal-status check answers first and the transition is never reached, which is exactly how an earlier
    ///     version of this test passed with the compare-and-set removed.
    /// </remarks>
    [Test]
    public async Task StartLive_WhenAnotherWriterFinishesTheRowMidRead_RefusesInsteadOfResurrectingIt()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        _ = registry.IsLive(Arg.Any<Guid>()).Returns(false);

        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);
        var created = await harness.Service
                                   .CreateSessionAsync(new CreateTranscriptionSessionInput
                                   {
                                       SourceKind = "Microphone"
                                   }, CancellationToken.None);

        // The start reads Created and is then parked inside that read.
        harness.ReadGate.Arm();
        var starting = harness.Service.StartLiveAsync(created.Id, CancellationToken.None);
        await harness.ReadGate.Entered.WaitAsync(TestBudgets.Contended);

        // A graceful end lands while it is parked, so the status it decided on is already stale.
        await harness.Service
                     .CompleteLiveAsync(created.Id, TranscriptionSessionStatus.Completed, durationMs: 1_000, "en", errorCode: null, errorMessage: null, CancellationToken.None);
        harness.ReadGate.Release();

        var result = await starting;

        AssertEx.Equal(StartLiveOutcome.SessionAlreadyFinished, result.Outcome,
            "A start that lost the row must say so, not write Transcribing over a terminal status.");
        AssertEx.Equal(TranscriptionSessionStatus.Completed, result.Status, "And report what the row actually says now.");
        var row = AssertEx.NotNull(await harness.Service.GetSessionAsync(created.Id, CancellationToken.None), "The row exists.");
        AssertEx.Equal(TranscriptionSessionStatus.Completed, row.Status, "A finished session must not be resurrected by a start that arrived late.");
        await registry.DidNotReceiveWithAnyArgs().StartLiveSessionAsync(Guid.Empty, null!, default);
    }

    [Test]
    public async Task StartLive_OnACreatedRow_TransitionsItToTranscribing()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        _ = registry.IsLive(Arg.Any<Guid>()).Returns(false);

        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);
        var created = await harness.Service
                                   .CreateSessionAsync(new CreateTranscriptionSessionInput
                                   {
                                       SourceKind = "Microphone"
                                   }, CancellationToken.None);

        var result = await harness.Service.StartLiveAsync(created.Id, CancellationToken.None);

        AssertEx.Equal(StartLiveOutcome.Started, result.Outcome, "The ordinary path still starts.");
        var row = AssertEx.NotNull(await harness.Service.GetSessionAsync(created.Id, CancellationToken.None), "The row exists.");
        AssertEx.Equal(TranscriptionSessionStatus.Transcribing, row.Status, "The compare-and-set moved it.");
        await registry.Received(1).StartLiveSessionAsync(created.Id, Arg.Any<LiveSessionOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteSession_WhileAnEndIsAlreadyRunning_JoinsItBeforeRemovingTheRow()
    {
        var registry = Substitute.For<ILiveTranscriptionSessionRegistry>();
        var ending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // False is the realistic answer, not a contrived one: admission closes as the first step of ending, so a
        // delete arriving mid-teardown always sees a session that no longer reads as live.
        _ = registry.IsLive(Arg.Any<Guid>()).Returns(false);
        _ = registry.EndAsync(Arg.Any<Guid>(), Arg.Any<LiveEndReason>(), Arg.Any<CancellationToken>()).Returns(_ => ending.Task);

        await using var harness = await TranscriptionServiceHarness.CreateAsync(registry);
        var created = await harness.Service
                                   .CreateSessionAsync(new CreateTranscriptionSessionInput
                                   {
                                       SourceKind = "Microphone"
                                   }, CancellationToken.None);

        var delete = harness.Service.DeleteSessionAsync(created.Id, CancellationToken.None);

        await AssertEx.StaysIncompleteAsync(delete, "Delete must join the in-flight end, not remove the row from under lanes that are still running.");
        await registry.Received(1).EndAsync(created.Id, LiveEndReason.Cancelled, Arg.Any<CancellationToken>());
        AssertEx.NotNull(await harness.Service.GetSessionAsync(created.Id, CancellationToken.None),
            "The row is still there while the teardown runs.");

        ending.SetResult();
        await delete;

        AssertEx.Null(await harness.Service.GetSessionAsync(created.Id, CancellationToken.None), "And gone once it finished.");
    }

    [Test]
    public async Task AttachProducer_WhileTheSessionIsEnding_IsRefused()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var first = new RecordingAudioProducer
        {
            Hangs = true
        };
        var late = new RecordingAudioProducer();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        first.Token = fixture.Registry.AttachProducer(sessionId, first).ProducerToken;

        // Parked inside the first producer's StopAsync: admission is closed and the entry is STILL registered, which
        // is the only window in which the registry could accept a producer the stop step has already walked past.
        // Once the end completes the entry is gone and the unknown-session check would answer instead, so a test that
        // attaches after EndAsync returns proves nothing about this guard.
        var ending = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await first.Entered.WaitAsync(TestBudgets.Contended);

        _ = AssertEx.Throws<InvalidOperationException>(() => fixture.Registry.AttachProducer(sessionId, late),
            "A producer attached while the session is ending would capture forever: nothing left in the teardown will stop it.");

        first.Release();
        await ending;

        AssertEx.Equal(expected: 0, late.StopCount, "Nothing was ever going to stop the late producer, which is why it had to be refused.");
        AssertEx.Equal(expected: 1, first.StopCount, "The producer that attached in time is still stopped exactly once.");
    }

    [Test]
    public async Task ThrowingCancellationCallback_DoesNotPreventFinalization()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var id = Guid.NewGuid();
        var producer = new RecordingAudioProducer();
        var statuses = fixture.RecordStatuses();
        await fixture.Registry.StartLiveSessionAsync(id, LiveOptions(), CancellationToken.None);
        producer.Token = fixture.Registry.AttachProducer(id, producer).ProducerToken;
        using var callback = producer.Token.Register(static () => throw new InvalidOperationException("Cancellation callback failed."));
        await PushAsync(fixture.Registry, id, TranscriptChannel.Mono, 0, 500);

        await AssertEx.CompletesAsync(fixture.Registry.EndAsync(id, LiveEndReason.Completed, CancellationToken.None),
            TestBudgets.Contended, "A throwing cancellation callback must not bypass the terminal write.");
        await AssertEx.EventuallyAsync(() => fixture.Logger.CountContaining("Cancelling live transcription resources failed.") == 1,
            TestBudgets.Contended, "The asynchronous callback failure is observed and reported.");
        AssertEx.True(producer.TokenWasCancelledAtStop);
        AssertEx.Equal(1, producer.StopCount);
        AssertEx.Equal("Completed", string.Join(',', Snapshot(statuses)));
        AssertEx.False(fixture.Registry.IsRegistered(id));
    }

    [Test]
    public async Task EndAsync_CancelsTheProducerTokenAndAwaitsStopAsync()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var producer = new RecordingAudioProducer();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        var registration = fixture.Registry.AttachProducer(sessionId, producer);
        producer.Token = registration.ProducerToken;

        AssertEx.False(registration.ProducerToken.IsCancellationRequested, "A live session's producer runs on an uncancelled token.");
        await fixture.Registry.EndAsync(sessionId, LiveEndReason.Cancelled, CancellationToken.None);

        AssertEx.True(producer.TokenWasCancelledAtStop,
            "A producer that already stops on its token has nothing left for StopAsync to do, so the token is cancelled first.");
        AssertEx.Equal(expected: 1, producer.StopCount, "And StopAsync was still awaited.");
        AssertEx.True(registration.ProducerToken.IsCancellationRequested);
    }

    [Test]
    public async Task EndAsync_WithAProducerThatHangs_AbandonsItAfterTheBoundAndStillCompletes()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var producer = new RecordingAudioProducer
        {
            Hangs = true
        };

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        producer.Token = fixture.Registry.AttachProducer(sessionId, producer).ProducerToken;

        var ending = fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
        await producer.Entered.WaitAsync(TestBudgets.Contended);
        await AssertEx.StaysIncompleteAsync(ending, "The bound has not elapsed yet, so the session is still waiting for its producer.");

        fixture.Time.Advance(ProducerStopBound);

        await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "A producer that hangs must be abandoned, never allowed to hold the session open.");
        await fixture.Publisher.Received(1).PublishStatusAsync(sessionId, LiveEndReason.Completed, Arg.Any<CancellationToken>());

        producer.Release();
    }

    [Test]
    public async Task EndAsync_DuringBlockedInference_StopsTheNativeProducerPromptly()
    {
        var transcriber = new GatedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var producer = new RecordingAudioProducer();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        producer.Token = fixture.Registry.AttachProducer(sessionId, producer).ProducerToken;
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        await transcriber.Entered.WaitAsync(TestBudgets.Contended);

        var ending = fixture.Registry.EndAsync(sessionId, LiveEndReason.Cancelled, CancellationToken.None);

        // The whole point: the capture hardware is released while the model is still thinking, not after it answers.
        await AssertEx.EventuallyAsync(() => producer.StopCount == 1, TestBudgets.Contended,
            "The native producer waited behind an inference it has nothing to do with.");
        AssertEx.False(transcriber.Released, "The gate was still closed when the producer stopped.");

        await AssertEx.CompletesAsync(ending, TestBudgets.Contended, "And the session still finished ending.");
    }

    [Test]
    public async Task Partial_IsPublishedOnlyWhenTheTextChanged()
    {
        // A guard wider than the window holds every returned segment back, so each tick produces the same provisional
        // text and nothing ever commits.
        var transcriber = new ScriptedWhisperTranscriber(static _ =>
        [
            new WhisperTranscriptSegment
            {
                StartSeconds = 0.0,
                EndSeconds = 0.1,
                Text = "still talking",
                Confidence = 0.9
            }
        ]);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        await fixture.Registry
                     .StartLiveSessionAsync(sessionId,
                         LiveOptions(settings: new LiveSegmenterSettings
                         {
                             MaxWindowSeconds = 10,
                             TailGuardMs = 5_000,
                             TickMs = 1_000
                         }),
                         CancellationToken.None);

        // One frame, so nothing is ever queued behind it: a burst of small frames would read as a lane catching up,
        // which skips ticks by design, and whether it did would depend on scheduling.
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 2_500, frameMs: 2_500);
        await AssertEx.EventuallyAsync(() => transcriber.CallCount == 2, TestBudgets.Contended, "Two ticks fired.");
        await AssertEx.SettleAsync();

        await fixture.Publisher.Received(1)
                     .PublishPartialAsync(sessionId, TranscriptChannel.Mono, "still talking", Arg.Any<CancellationToken>());
        await fixture.Publisher.DidNotReceive()
                     .PublishSegmentAsync(Arg.Any<Guid>(),
                         Arg.Any<long>(),
                         Arg.Any<TranscriptChannel>(),
                         Arg.Any<long>(),
                         Arg.Any<long>(),
                         Arg.Any<string>(),
                         Arg.Any<double?>(),
                         Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AbandonedSession_IsCancelledAfterTheGrace()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        var statuses = fixture.RecordStatuses();
        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        fixture.Registry.NoteBrowserAttached(sessionId, "connection-1");
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 1_000);
        fixture.Registry.NoteBrowserDetached(sessionId, "connection-1");

        AssertEx.True(fixture.Registry.IsLive(sessionId), "The grace has not elapsed yet.");
        fixture.Time.Advance(TimeSpan.FromSeconds(GraceSeconds));

        await WaitForEndAsync(statuses, LiveEndReason.Abandoned);
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Cancelled,
                         Arg.Any<long>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AbandonedSession_ResumedInsideTheGrace_IsNotCancelled()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        fixture.Registry.NoteBrowserAttached(sessionId, "connection-1");
        fixture.Registry.NoteBrowserDetached(sessionId, "connection-1");

        // A reconnect inside the window: a different connection id, because the browser opened a new socket.
        fixture.Registry.NoteBrowserAttached(sessionId, "connection-2");

        fixture.Time.Advance(TimeSpan.FromSeconds(GraceSeconds * 4));
        await AssertEx.SettleAsync();

        AssertEx.True(fixture.Registry.IsLive(sessionId), "Reattaching disarms the grace rather than merely postponing it.");
        await fixture.Publisher.DidNotReceive()
                     .PublishStatusAsync(Arg.Any<Guid>(), Arg.Any<LiveEndReason>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task End_FlushesEveryLaneAndMarksTheSessionCompleted()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(channels: TwoLanes), CancellationToken.None);

        // Half a tick into each lane: nothing has been submitted, so only the flush can produce these segments.
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.You, fromMs: 0, toMs: 500);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Others, fromMs: 0, toMs: 500);
        AssertEx.Equal(expected: 0, transcriber.CallCount, "A sub-tick push submits nothing on its own.");

        await fixture.Registry.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);

        // EndAsync awaits every lane's flush, but the lanes flush concurrently: whichever reaches the commit gate first
        // takes seq 1. Pin what the contract fixes (one gap-free counter, published in order) apart from which lane won.
        var segments = Snapshot(published);
        AssertEx.Equal("1,2", string.Join(',', segments.Select(static segment => segment.Seq)),
            "Both flushed tails share one gap-free counter and are published in sequence order.");
        AssertEx.Equal("You|0-500|w0-500;Others|0-500|w0-500",
            string.Join(';',
                segments.OrderBy(static segment => (int)segment.Channel)
                        .Select(static segment => $"{segment.Channel}|{segment.StartMs}-{segment.EndMs}|{segment.Text}")),
            "Every lane is flushed, and the tail each one was holding back becomes durable.");
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Completed,
                         durationMs: 500L,
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TwoLanes_CommitIndependentlyAndSegmentsOrderByStartMsWithTheirChannel()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        await using var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();
        var published = fixture.RecordSegments();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(channels: TwoLanes), CancellationToken.None);

        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.You, fromMs: 0, toMs: 1_000);
        await WaitForSegmentsAsync(published, count: 1);
        // One frame spanning two ticks, so the lane is never behind and both ticks submit.
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Others, fromMs: 0, toMs: 2_000, frameMs: 2_000);
        var segments = await WaitForSegmentsAsync(published, count: 3);

        // Sequence is commit order; start time is speech order. Both are right, for different readers.
        AssertEx.Equal("1|You|0-1000;2|Others|0-1000;3|Others|1000-2000",
            string.Join(';', segments.Select(static segment => $"{segment.Seq}|{segment.Channel}|{segment.StartMs}-{segment.EndMs}")),
            "Each lane keeps its own audio clock and its own channel; the counter is the only thing they share.");
        AssertEx.Equal("You@0,Others@0,Others@1000",
            string.Join(',',
                segments.OrderBy(static segment => segment.StartMs)
                        .ThenBy(static segment => (int)segment.Channel)
                        .Select(static segment => $"{segment.Channel}@{segment.StartMs}")),
            "Ordered by speech time the two lanes interleave, which is what the session view renders.");
    }

    [Test]
    public async Task DisposeAsync_MarksAStillOpenSessionCancelled()
    {
        var transcriber = new ScriptedWhisperTranscriber(OneSegment);
        var fixture = new RegistryFixture(transcriber);
        var sessionId = Guid.NewGuid();

        await fixture.Registry.StartLiveSessionAsync(sessionId, LiveOptions(), CancellationToken.None);
        await PushAsync(fixture.Registry, sessionId, TranscriptChannel.Mono, fromMs: 0, toMs: 500);

        await fixture.DisposeAsync();

        AssertEx.False(fixture.Registry.IsLive(sessionId), "Shutting down releases every session it still holds.");
        await fixture.Service.Received(1)
                     .CompleteLiveAsync(sessionId,
                         TranscriptionSessionStatus.Cancelled,
                         Arg.Any<long>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<CancellationToken>());
        await fixture.Publisher.Received(1).PublishStatusAsync(sessionId, LiveEndReason.Cancelled, Arg.Any<CancellationToken>());
    }

    private static readonly TranscriptChannel[] MonoLane = [TranscriptChannel.Mono];
    private static readonly TranscriptChannel[] TwoLanes = [TranscriptChannel.You, TranscriptChannel.Others];

    private static IReadOnlyList<WhisperTranscriptSegment> OneSegment(SubmittedWindow window) =>
    [
        new WhisperTranscriptSegment
        {
            StartSeconds = 0.0,
            EndSeconds = window.DurationMs / 1000.0,
            Text = $"w{window.StartMs}-{window.EndMs}",
            Confidence = 0.9
        }
    ];

    /// <summary>A producer that never pushes and records whether it was asked to stop.</summary>
    private sealed class SilentProducer : ILiveAudioProducer
    {
        public bool Stopped { get; private set; }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            return ValueTask.CompletedTask;
        }
    }

    private static LiveSessionOptions LiveOptions(bool persist = true,
        TranscriptionSourceKind sourceKind = TranscriptionSourceKind.Microphone,
        TranscriptChannel[]? channels = null,
        long startingSeq = 0,
        LiveSegmenterSettings? settings = null) =>
        new()
        {
            ModelId = ModelId,
            // A zero tail guard makes one tick produce one commit, so every assertion below is about the pipeline
            // rather than about when the segmenter decides a word is finished — that is the segmenter's own suite.
            Settings = settings ?? new LiveSegmenterSettings
            {
                MaxWindowSeconds = 2,
                TailGuardMs = 0,
                TickMs = 1_000
            },
            Channels = channels ?? MonoLane,
            StartingSeq = startingSeq,
            SourceKind = sourceKind,
            Persist = persist
        };

    private static async Task PushAsync(ILiveTranscriptionSessionRegistry registry,
        Guid sessionId,
        TranscriptChannel channel,
        long fromMs,
        long toMs,
        int frameMs = 500)
    {
        for (var at = fromMs; at < toMs; at += frameMs)
        {
            var until = Math.Min(at + frameMs, toMs);
            await registry.PushAudioAsync(sessionId, channel, LivePcm.Range(at, until), CancellationToken.None);
        }
    }

    /// <summary>
    ///     Waits until the session has published its end status, which is the LAST step of ending.
    /// </summary>
    /// <remarks>
    ///     Not <see cref="ILiveTranscriptionSessionRegistry.IsLive" />: admission closes as the first step, so a
    ///     session reads as not live long before it has stopped its producer or terminalized its row.
    /// </remarks>
    private static async Task WaitForEndAsync(List<LiveEndReason> statuses, LiveEndReason reason) =>
        await AssertEx.EventuallyAsync(() => Snapshot(statuses).Contains(reason),
            TestBudgets.Contended,
            $"The session never finished ending as {reason}; statuses seen: [{string.Join(',', Snapshot(statuses))}].");

    /// <summary>Waits until <paramref name="count" /> commits have been published, then snapshots them.</summary>
    private static async Task<IReadOnlyList<PublishedSegment>> WaitForSegmentsAsync(List<PublishedSegment> recorded, int count)
    {
        await AssertEx.EventuallyAsync(() => Snapshot(recorded).Count >= count,
            TestBudgets.Contended,
            $"Expected {count} published commits; a frame is queued onto its lane, so its effect lands after the push returns.");
        return Snapshot(recorded);
    }

    private static string Describe(PublishedSegment segment) =>
        $"{segment.Seq}|{segment.Channel}|{segment.StartMs}-{segment.EndMs}|{segment.Text}";

    private static List<T> Snapshot<T>(List<T> recorded)
    {
        lock (recorded)
        {
            return [.. recorded];
        }
    }

    /// <summary>One published commit, captured off the publisher substitute.</summary>
    private sealed record PublishedSegment
    {
        public required long Seq { get; init; }

        public required TranscriptChannel Channel { get; init; }

        public required long StartMs { get; init; }

        public required long EndMs { get; init; }

        public required string Text { get; init; }
    }

    /// <summary>
    ///     A registry wired to substituted collaborators, a manual clock and a real segmenter per lane.
    /// </summary>
    /// <remarks>
    ///     The service is resolved through a real scope factory, exactly as the registry resolves it in the host, so
    ///     the "no constructor cycle" arrangement is exercised rather than assumed.
    /// </remarks>
    private sealed class RegistryFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public RegistryFixture(IWhisperTranscriber transcriber)
        {
            Service = Substitute.For<ITranscriptionService>();
            Publisher = Substitute.For<ITranscriptionEventPublisher>();
            Time = new ManualTimeProvider();

            var services = new ServiceCollection();
            _ = services.AddSingleton(Service);
            _provider = services.BuildServiceProvider();

            Logger = new RecordingLogger<LiveTranscriptionSessionRegistry>();
            Registry = new LiveTranscriptionSessionRegistry(transcriber,
                Publisher,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new TranscriptionOptions
                {
                    AbandonedSessionGraceSeconds = GraceSeconds
                }),
                Time,
                Logger);
        }

        public ITranscriptionService Service { get; }

        public ITranscriptionEventPublisher Publisher { get; }

        public ManualTimeProvider Time { get; }

        public LiveTranscriptionSessionRegistry Registry { get; }

        /// <summary>
        ///     The registry's own log. Some of what the registry does for correctness — dropping a result that
        ///     arrived after finalization — is deliberately invisible on every other seam, so the warning it writes
        ///     is the only thing a test can wait on to know that path ran.
        /// </summary>
        public RecordingLogger<LiveTranscriptionSessionRegistry> Logger { get; }

        /// <summary>Captures every published commit, in publication order.</summary>
        public List<PublishedSegment> RecordSegments()
        {
            var recorded = new List<PublishedSegment>();
            _ = Publisher.PublishSegmentAsync(Arg.Any<Guid>(),
                             Arg.Any<long>(),
                             Arg.Any<TranscriptChannel>(),
                             Arg.Any<long>(),
                             Arg.Any<long>(),
                             Arg.Any<string>(),
                             Arg.Any<double?>(),
                             Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             lock (recorded)
                             {
                                 recorded.Add(new PublishedSegment
                                 {
                                     Seq = call.ArgAt<long>(1),
                                     Channel = call.ArgAt<TranscriptChannel>(2),
                                     StartMs = call.ArgAt<long>(3),
                                     EndMs = call.ArgAt<long>(4),
                                     Text = call.ArgAt<string>(5)
                                 });
                             }

                             return Task.CompletedTask;
                         });
            return recorded;
        }

        /// <summary>
        ///     Captures every published end status.
        /// </summary>
        /// <remarks>
        ///     Tests wait on this rather than on <see cref="ILiveTranscriptionSessionRegistry.IsLive" />: admission
        ///     closes as the FIRST step of ending, so a session reads as not live long before it has stopped its
        ///     producer, flushed its lanes or terminalized its row.
        /// </remarks>
        public List<LiveEndReason> RecordStatuses()
        {
            var recorded = new List<LiveEndReason>();
            _ = Publisher.PublishStatusAsync(Arg.Any<Guid>(), Arg.Any<LiveEndReason>(), Arg.Any<CancellationToken>())
                         .Returns(call =>
                         {
                             lock (recorded)
                             {
                                 recorded.Add(call.ArgAt<LiveEndReason>(1));
                             }

                             return Task.CompletedTask;
                         });
            return recorded;
        }

        /// <summary>
        ///     One ordered log across a hand-written producer and two substitutes, because the assertion spans all
        ///     three and <c>Received.InOrder</c> only sees substituted calls.
        /// </summary>
        public List<string> RecordOrder()
        {
            var order = new List<string>();

            _ = Service.AppendLiveSegmentAsync(Arg.Any<Guid>(),
                           Arg.Any<long>(),
                           Arg.Any<TranscriptChannel>(),
                           Arg.Any<long>(),
                           Arg.Any<long>(),
                           Arg.Any<string>(),
                           Arg.Any<double?>(),
                           Arg.Any<CancellationToken>())
                       .Returns(_ => Note(order, "append"));
            _ = Service.CompleteLiveAsync(Arg.Any<Guid>(),
                           Arg.Any<TranscriptionSessionStatus>(),
                           Arg.Any<long>(),
                           Arg.Any<string?>(),
                           Arg.Any<string?>(),
                           Arg.Any<string?>(),
                           Arg.Any<CancellationToken>())
                       .Returns(_ => Note(order, "complete"));
            _ = Publisher.PublishStatusAsync(Arg.Any<Guid>(), Arg.Any<LiveEndReason>(), Arg.Any<CancellationToken>())
                         .Returns(_ => Note(order, "status"));

            return order;
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await _provider.DisposeAsync();
        }

        private static Task Note(List<string> order, string step)
        {
            lock (order)
            {
                order.Add(step);
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>
///     A transcriber that parks every call on a gate the test opens, and honours the cancellation token while it
///     waits — which is what makes "ending a session does not wait behind inference" an assertion rather than a hope.
/// </summary>
internal sealed class GatedWhisperTranscriber : IWhisperTranscriber
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ScriptedWhisperTranscriber _inner;
    private int _callCount;

    public GatedWhisperTranscriber(Func<SubmittedWindow, IReadOnlyList<WhisperTranscriptSegment>> respond)
    {
        _inner = new(respond);
    }

    /// <summary>Completes once a call has reached the gate.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Whether the gate has been opened, so a test can assert an effect happened while it was still shut.</summary>
    public bool Released { get; private set; }

    /// <summary>
    ///     How many calls have ENTERED, not how many have answered: a submission parked on the gate has already
    ///     reached the transcriber, and counting completions would report zero for exactly the state under test.
    /// </summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>The absolute ranges that actually reached the transcriber, decoded from the submitted bytes.</summary>
    public IReadOnlyList<SubmittedWindow> Windows => _inner.Windows;

    public void Release()
    {
        Released = true;
        _ = _gate.TrySetResult();
    }

    /// <summary>
    ///     When set, the gate ignores the cancellation token entirely — a transcriber wedged past the point where
    ///     the session gave up on it, which is the only way to observe what happens to its late answer.
    /// </summary>
    public bool IgnoresCancellation { get; init; }

    /// <summary>
    ///     When set, only the FIRST call parks; every later one answers immediately. That is one wedged lane beside
    ///     healthy ones — a single shared gate would wedge every lane of the session instead.
    /// </summary>
    public bool GatesFirstCallOnly { get; init; }

    public async Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct)
    {
        // Checked before the call is counted, the way a real HTTP call refuses an already-cancelled token: once the
        // session aborts, the frames still queued behind the gate must not read as submissions that happened.
        if (!IgnoresCancellation)
        {
            ct.ThrowIfCancellationRequested();
        }

        var ordinal = Interlocked.Increment(ref _callCount);
        _ = _entered.TrySetResult();

        if (GatesFirstCallOnly && ordinal > 1)
        {
            return await _inner.TranscribeAsync(modelId, request, ct);
        }

        if (IgnoresCancellation)
        {
            await _gate.Task;

            // CancellationToken.None all the way down: a fake that parks past the abort but then lets the inner
            // call throw on the aborted token would never produce the late answer this mode exists to produce.
            return await _inner.TranscribeAsync(modelId, request, CancellationToken.None);
        }

        await _gate.Task.WaitAsync(ct);
        return await _inner.TranscribeAsync(modelId, request, ct);
    }
}

/// <summary>
///     An in-host audio producer that records how it was stopped: whether its token had already been cancelled, how
///     many times it was asked, and optionally refusing to return at all.
/// </summary>
internal sealed class RecordingAudioProducer : ILiveAudioProducer
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string>? _order;
    private int _stopCount;

    public RecordingAudioProducer(List<string>? order = null)
    {
        _order = order;
    }

    /// <summary>The token the registry handed back at attachment; the test wires it so the fake can inspect it.</summary>
    public CancellationToken Token { get; set; }

    /// <summary>When set, <see cref="StopAsync" /> never returns until <see cref="Release" />, ignoring its own bound.</summary>
    public bool Hangs { get; init; }

    public int StopCount => Volatile.Read(ref _stopCount);

    /// <summary>Whether the producer token was already cancelled by the time stopping was asked for.</summary>
    public bool TokenWasCancelledAtStop { get; private set; }

    /// <summary>Completes once <see cref="StopAsync" /> has been entered.</summary>
    public Task Entered => _entered.Task;

    public void Release() =>
        _ = _release.TrySetResult();

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        TokenWasCancelledAtStop = Token.IsCancellationRequested;
        _ = Interlocked.Increment(ref _stopCount);

        if (_order is not null)
        {
            lock (_order)
            {
                _order.Add("stop");
            }
        }

        _ = _entered.TrySetResult();

        if (Hangs)
        {
            // Deliberately not linked to the caller's token: the registry's own bound is what has to release it.
            await _release.Task;
        }
    }
}

/// <summary>Captures formatted log messages so a test can wait on a code path that has no other observable effect.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) =>
        true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_messages)
        {
            _messages.Add(formatter(state, exception));
        }
    }

    /// <summary>How many messages contain <paramref name="fragment" />.</summary>
    public int CountContaining(string fragment)
    {
        lock (_messages)
        {
            return _messages.Count(message => message.Contains(fragment, StringComparison.Ordinal));
        }
    }
}
