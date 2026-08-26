namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkExecutionPrimitivesTests
{
    [Test]
    public async Task ContextAdmission_RejectsUnknownAndInsufficientContext_AndRecordsEffectiveValue()
    {
        var policy = new BenchmarkContextAdmissionPolicy(8192);

        var unknown = await policy.EvaluateAsync(Context(effective: null));
        var insufficient = await policy.EvaluateAsync(Context(effective: 4096));

        AssertEx.False(unknown.IsAllowed);
        AssertEx.Equal(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable, unknown.RejectionReasonCode);
        AssertEx.False(insufficient.IsAllowed);
        AssertEx.Equal(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient, insufficient.RejectionReasonCode);
        AssertEx.Equal<int?>(4096, policy.EffectiveContextTokens);
    }

    [Test]
    public async Task ContextAdmission_AllowsExactRequiredContext()
    {
        var policy = new BenchmarkContextAdmissionPolicy(8192);

        var decision = await policy.EvaluateAsync(Context(effective: 8192));

        AssertEx.True(decision.IsAllowed);
        AssertEx.Equal<int?>(8192, policy.EffectiveContextTokens);
    }

    [Test]
    public void EventBuffer_TrimsByCountAndReturnsResetForCursorBeforeRetainedHistory()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 2, maxBytes: 4096);
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "one"));
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "two"));
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "three"));

        var reset = buffer.Replay(runId, afterSequence: 0, runVersion: 7);
        var retained = buffer.Replay(runId, afterSequence: 1, runVersion: 7);

        AssertEx.True(reset.ResetRequired);
        AssertEx.Equal(3L, reset.LatestSequence);
        AssertEx.False(retained.ResetRequired);
        AssertEx.True(retained.Events.Select(static item => item.Sequence).SequenceEqual([2L, 3L]));
    }

    [Test]
    public void EventBuffer_DeduplicatesReservedSequenceAndEvictsPlaintextOnTerminal()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 8, maxBytes: 4096);
        var published = 0;
        buffer.EventPublished += (_, _) => published++;
        var streamEvent = buffer.Reserve(runId,
            BenchmarkRunStreamEventKind.OutputDelta,
            new BenchmarkRunStreamPayload(Content: "sensitive"));

        buffer.PublishReserved(streamEvent);
        buffer.PublishReserved(streamEvent);
        buffer.EvictPlaintext(runId);
        var replay = buffer.Replay(runId, streamEvent.Sequence, runVersion: 9);

        AssertEx.Equal(1, published);
        AssertEx.True(replay.ResetRequired);
        AssertEx.Empty(replay.Events);
        AssertEx.Equal(streamEvent.Sequence, replay.LatestSequence);
    }

    [Test]
    public void EventBuffer_JudgePhaseReopensReplayAfterPrimaryPlaintextEviction()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 8, maxBytes: 4096);
        _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "primary"));
        var primaryTerminal = buffer.Append(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkPrimaryStatus.Succeeded.ToString()));
        buffer.EvictPlaintext(runId);

        buffer.BeginActivePhase(runId, primaryTerminal.Sequence);
        var judgeRunning = buffer.Append(runId,
            BenchmarkRunStreamEventKind.JudgeState,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Running.ToString()));

        var current = buffer.Replay(runId, primaryTerminal.Sequence, runVersion: 3);
        var stale = buffer.Replay(runId, primaryTerminal.Sequence - 1, runVersion: 3);
        AssertEx.False(current.ResetRequired);
        AssertEx.Equal(1, current.Events.Count);
        AssertEx.Equal(judgeRunning.Sequence, current.Events[0].Sequence);
        AssertEx.True(stale.ResetRequired);

        var judgeTerminal = buffer.Append(runId,
            BenchmarkRunStreamEventKind.TerminalSnapshotAvailable,
            new BenchmarkRunStreamPayload(State: BenchmarkRunJudgeStates.Succeeded.ToString()));
        buffer.EvictPlaintext(runId);
        var terminalReplay = buffer.Replay(runId, judgeTerminal.Sequence, runVersion: 4);
        AssertEx.True(terminalReplay.ResetRequired);
        AssertEx.Empty(terminalReplay.Events);
        AssertEx.Equal(judgeTerminal.Sequence, terminalReplay.LatestSequence);
    }

    [Test]
    public void EventBuffer_TrimsSingleOversizedUtf8Payload()
    {
        var runId = Guid.NewGuid();
        var buffer = Buffer(maxEvents: 8, maxBytes: 64);

        var streamEvent = buffer.Append(runId,
            BenchmarkRunStreamEventKind.OutputDelta,
            new BenchmarkRunStreamPayload(Content: new string('\u20ac', 128)));
        var replay = buffer.Replay(runId, afterSequence: 0, runVersion: 1);

        AssertEx.True(replay.ResetRequired);
        AssertEx.Empty(replay.Events);
        AssertEx.Equal(streamEvent.Sequence, replay.LatestSequence);
    }

    [Test]
    public void CancellationRegistry_OwnsOneRegistrationAndSignalsOnlyMatchingWork()
    {
        var registry = new BenchmarkCancellationRegistry();
        var runId = Guid.NewGuid();
        using var primary = registry.Register(runId, BenchmarkWorkKind.Primary, CancellationToken.None);
        using var judge = registry.Register(runId, BenchmarkWorkKind.Judge, CancellationToken.None);

        var signalled = registry.TryCancel(runId, BenchmarkWorkKind.Judge);

        AssertEx.True(signalled);
        AssertEx.False(primary.Token.IsCancellationRequested);
        AssertEx.True(judge.Token.IsCancellationRequested);
        _ = AssertEx.Throws<InvalidOperationException>(() => registry.Register(runId, BenchmarkWorkKind.Primary, CancellationToken.None));
    }

    [Test]
    public async Task CancellationService_RunningPrimaryPersistsRequestThenSignalsOwnedToken()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, BenchmarkRunJudgeStates.None, version: 4);
        var requested = run with
        {
            PrimaryStatus = BenchmarkPrimaryStatus.CancelRequested,
            Version = 5
        };
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        store.CancelAsync(run.Id, run.Version, Arg.Any<CancellationToken>()).Returns(requested);
        var registry = new BenchmarkCancellationRegistry();
        using var registration = registry.Register(run.Id, BenchmarkWorkKind.Primary, CancellationToken.None);
        var service = new BenchmarkCancellationService(store, registry);

        var result = await service.CancelAsync(run.Id, run.Version, BenchmarkCancellationTarget.Primary);

        AssertEx.Equal(BenchmarkPrimaryStatus.CancelRequested, result.PrimaryStatus);
        AssertEx.True(registration.Token.IsCancellationRequested);
        _ = store.Received(1).CancelAsync(run.Id, run.Version, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancellationService_RejectsJudgeCancellationWhilePrimaryIsRunning()
    {
        var run = Run(BenchmarkPrimaryStatus.Running, BenchmarkRunJudgeStates.None, version: 4);
        var store = Substitute.For<IBenchmarkStore>();
        store.GetRunAsync(run.Id, Arg.Any<CancellationToken>()).Returns(run);
        var service = new BenchmarkCancellationService(store, new BenchmarkCancellationRegistry());

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
            service.CancelAsync(run.Id, run.Version, BenchmarkCancellationTarget.Judge));

        AssertEx.Equal("JudgeNotCancellable", exception.Code);
        _ = store.DidNotReceive().CancelAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void EventBuffer_DropsTheOldestTerminalRunsButKeepsTheReplayResetForRecentOnes()
    {
        // The leak: one entry per run, forever. A node that runs a thousand benchmarks kept a thousand of them, each
        // holding sequence bookkeeping for a stream that ended hours ago.
        var buffer = Buffer(maxEvents: 8, maxBytes: 4096, maxTerminalRuns: 2);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        foreach (var runId in new[]
                 {
                     first,
                     second,
                     third
                 })
        {
            _ = buffer.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "out"));
            buffer.EvictPlaintext(runId);
        }

        AssertEx.Equal(expected: 2, buffer.TrackedRunCount, "Only the cap's worth of terminal runs may be remembered.");

        // The recent ones still answer "reset" — that is the whole reason an emptied entry is kept at all.
        AssertEx.True(buffer.Replay(third, afterSequence: 0, runVersion: 1).ResetRequired);
        AssertEx.True(buffer.Replay(second, afterSequence: 0, runVersion: 1).ResetRequired);

        // The dropped one is answered as unknown. The hub does NOT read that as "nothing happened": it compares the
        // run's persisted LastStreamSequence against this LatestSequence and resets on the difference, which is the
        // same answer by another route.
        var forgotten = buffer.Replay(first, afterSequence: 0, runVersion: 1);
        AssertEx.False(forgotten.ResetRequired);
        AssertEx.Equal(expected: 0L, forgotten.LatestSequence, "An unknown run reports no sequence, which is what the hub's persisted-sequence check needs.");
    }

    [Test]
    public void EventBuffer_KeepsARunThatWentActiveAgainAfterItsPrimaryPhaseEnded()
    {
        // A run is evicted once per terminal PHASE, and it has two: the primary, then the judge. Dropping the entry
        // for a run that is streaming again would restart its sequence numbering under a live subscriber.
        var buffer = Buffer(maxEvents: 8, maxBytes: 4096, maxTerminalRuns: 1);
        var judged = Guid.NewGuid();
        _ = buffer.Append(judged, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "answer"));
        buffer.EvictPlaintext(judged);
        buffer.BeginActivePhase(judged, persistedSequence: 1);
        var judging = buffer.Append(judged, BenchmarkRunStreamEventKind.JudgeState, new BenchmarkRunStreamPayload(State: "running"));

        // Another run terminalizes and pushes the cap.
        var other = Guid.NewGuid();
        _ = buffer.Append(other, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "out"));
        buffer.EvictPlaintext(other);

        AssertEx.Equal(expected: 2L, judging.Sequence, "The judge phase continues the run's sequence, it does not restart it.");
        var replay = buffer.Replay(judged, afterSequence: 1, runVersion: 1);
        AssertEx.False(replay.ResetRequired, "A live phase must not be told to reset because another run ended.");
        AssertEx.Equal(expected: 2L, replay.LatestSequence);
    }

    [Test]
    public void QueueOptionsValidator_AcceptsThePositiveBoundedIntervalAndRejectsEverythingElse()
    {
        var validator = new BenchmarkQueueOptionsValidator();

        AssertEx.True(validator.Validate(name: null, new BenchmarkQueueOptions()).Succeeded, "the default poll interval must validate");
        AssertEx.True(validator.Validate(name: null,
                                   new BenchmarkQueueOptions
                                   {
                                       PollInterval = BenchmarkQueueOptions.MaxPollInterval
                                   })
                               .Succeeded,
            "the ceiling itself is a legal interval");
        AssertEx.True(validator.Validate(name: null,
                                   new BenchmarkQueueOptions
                                   {
                                       PollInterval = TimeSpan.Zero
                                   })
                               .Failed,
            "a zero interval would spin the queue");
        AssertEx.True(validator.Validate(name: null,
                                   new BenchmarkQueueOptions
                                   {
                                       PollInterval = BenchmarkQueueOptions.MaxPollInterval + TimeSpan.FromSeconds(1)
                                   })
                               .Failed,
            "an interval past the ceiling would strand queued work");
    }

    private static InvocationGenerationAdmissionContext Context(int? effective) =>
        new()
        {
            InvocationId = Guid.NewGuid(),
            RequestedContextTokens = 8192,
            EffectiveContextTokens = effective,
            ModelId = "model.gguf",
            ProviderName = "llamacpp"
        };

    private static BenchmarkEventBuffer Buffer(int maxEvents, int maxBytes, int? maxTerminalRuns = null) =>
        new(Options.Create(new BenchmarkEventBufferOptions
        {
            MaxEventCount = maxEvents,
            MaxUtf8Bytes = maxBytes,
            MaxRetainedTerminalRuns = maxTerminalRuns ?? BenchmarkEventBufferOptions.DefaultMaxRetainedTerminalRuns
        }));

    private static BenchmarkRunRecord Run(BenchmarkPrimaryStatus primary, string judgeState, long version) =>
        new(Guid.NewGuid(), Guid.NewGuid(), new byte[]
            {
                1
            }, "model.gguf", null, $"v1:{new string('a', 64)}", "Agent", 1, 8192,
            primary, null, null, null, null, null, 0, null, null, version, 1, 1, null, 1, null, null,
            PrimaryStopReason: null,
            Judge: new BenchmarkRunJudgeView(judgeState, null, null, null, null, null, null, null, null, PolicyCurrent: false, ExecutionCurrent: false, null));
}
