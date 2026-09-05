namespace XE_Local_AI_Engine.Tests.Integrations;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The replay-then-live cursor over the ring. Two properties carry the whole design and neither is obvious from the
///     signature: a HOLE left by an abandoned reservation is skipped in silence, and a PENDING reservation is a
///     BARRIER no reader may cross — a sequence published late would otherwise land below the reader's cursor and be
///     lost to every live consumer, with no gap to report it.
/// </summary>
public sealed class IntegrationExecutionEventBufferReadTests
{
    /// <summary>Test 1.</summary>
    [Test]
    public async Task ReadAsync_OnAFinishedExecution_ReplaysEverythingAndCompletesOnTheTerminal()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionAccepted);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionStarted);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.AssistantDelta);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);

        var events = await DrainAsync(buffer, executionId, sinceSequence: 0);

        AssertEx.True(events.Select(static streamEvent => streamEvent.Sequence).SequenceEqual([1L, 2L, 3L, 4L]));
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted, events[^1].Type, "The enumerator ends on the terminal event and nothing else.");
    }

    /// <summary>Test 2.</summary>
    [Test]
    public async Task ReadAsync_MidFlight_YieldsTheBacklogThenLiveAppendsThenCompletes()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionAccepted);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionStarted);

        await using var reader = new Reader(buffer, executionId, sinceSequence: 0);
        await reader.WaitForCountAsync(expected: 2);

        _ = Append(buffer, executionId, IntegrationStreamEventTypes.AssistantDelta);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.True(reader.Snapshot().Select(static streamEvent => streamEvent.Sequence).SequenceEqual([1L, 2L, 3L, 4L]));
    }

    /// <summary>Test 3.</summary>
    [Test]
    public async Task ReadAsync_WithASinceSequence_StartsStrictlyAfterIt()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        for (var index = 0; index < 4; index++)
        {
            _ = Append(buffer, executionId);
        }

        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);

        var events = await DrainAsync(buffer, executionId, sinceSequence: 3);

        AssertEx.True(events.Select(static streamEvent => streamEvent.Sequence).SequenceEqual([4L, 5L]),
            "sinceSequence is exclusive: a caller resuming at 3 must not be handed 3 again.");
    }

    /// <summary>Test 4.</summary>
    [Test]
    public async Task ReadAsync_BelowTheRetainedFloor_ThrowsAndTheWindowEdgeDoesNot()
    {
        // A capacity of two forces the trim that moves the floor, which is exactly the shape a slow consumer meets.
        using var buffer = CreateBuffer(capacity: 2);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);

        var floor = buffer.Floor(executionId);
        AssertEx.Equal(expected: 2L, floor, "Two of three events survive, so the oldest retained sequence is 2.");

        _ = await AssertEx.ThrowsAsync<IntegrationEventGapException>(() => DrainAsync(buffer, executionId, sinceSequence: 0));

        var edge = await DrainAsync(buffer, executionId, floor - 1);
        AssertEx.True(edge.Select(static streamEvent => streamEvent.Sequence).SequenceEqual([2L, 3L]),
            "A caller sitting exactly at Floor - 1 is served losslessly from Floor and must not be refused.");
    }

    /// <summary>Test 5.</summary>
    [Test]
    public async Task ReadAsync_AboveTheHead_ThrowsRatherThanParkingForever()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);

        _ = await AssertEx.ThrowsAsync<IntegrationEventGapException>(() => DrainAsync(buffer, executionId, buffer.LastSequence(executionId) + 1),
            "A stale id from another execution would otherwise hold an open 200 that never produces a frame.");
    }

    /// <summary>Test 6.</summary>
    [Test]
    public async Task ReadAsync_ForAnUntrackedExecution_ThrowsAndTheSentinelsReadZero()
    {
        using var buffer = CreateBuffer();
        var never = Guid.NewGuid();
        var evicted = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(evicted));
        _ = Append(buffer, evicted, IntegrationStreamEventTypes.ExecutionCompleted);
        buffer.Remove(evicted);

        foreach (var executionId in new[] { never, evicted })
        {
            AssertEx.False(buffer.IsTracked(executionId));
            AssertEx.Equal(expected: 0L, buffer.Floor(executionId));
            AssertEx.Equal(expected: 0L, buffer.LastSequence(executionId));
            _ = await AssertEx.ThrowsAsync<IntegrationEventGapException>(() => DrainAsync(buffer, executionId, sinceSequence: 0),
                "IsTracked is what separates a 404 from a 410; the numbers alone cannot, because both read (0, 0).");
        }
    }

    /// <summary>Test 7 — the lost-wakeup stress that guards capturing the wakeup source inside the snapshot's lock.</summary>
    [Test]
    public async Task ReadAsync_WhenAppendsRaceTheReadersPark_NeverMissesTheTerminal()
    {
        using var buffer = CreateBuffer(capacity: 64, maxTracked: 8);
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var executionId = Guid.NewGuid();
            AssertEx.True(buffer.TryCreate(executionId));
            _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionAccepted);

            var reading = DrainAsync(buffer, executionId, sinceSequence: 0);
            var writing = Task.Run(() =>
            {
                _ = Append(buffer, executionId);
                _ = Append(buffer, executionId);
                _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);
            });

            var events = await reading;
            await writing;

            AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted,
                events[^1].Type,
                $"Iteration {iteration}: an append landing between the snapshot and the await must complete the source the reader already holds.");
            buffer.Remove(executionId);
        }
    }

    /// <summary>Test 7a.</summary>
    [Test]
    public async Task ReadAsync_OverAnAbandonedHole_ReplaysEveryPublishedEventAndCompletes()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId);
        var abandoned = buffer.Reserve(executionId);
        buffer.Abandon(executionId, abandoned);
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);

        var events = await DrainAsync(buffer, executionId, sinceSequence: 0);

        AssertEx.True(events.Select(static streamEvent => streamEvent.Sequence).SequenceEqual([1L, 2L, 3L, 5L, 6L, 7L]),
            "A commit that failed leaves a permanent hole, and the sequences ascend with a jump rather than an error.");
        AssertEx.Equal(expected: 4L, abandoned);
    }

    /// <summary>Test 7b.</summary>
    [Test]
    public async Task ReadAsync_ResumingAtOrBelowAHole_YieldsEverythingAboveItWithoutRaising()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);
        var hole = buffer.Reserve(executionId);
        buffer.Abandon(executionId, hole);
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);

        var atTheHole = await DrainAsync(buffer, executionId, hole);
        var belowTheHole = await DrainAsync(buffer, executionId, hole - 1);

        AssertEx.True(atTheHole.Select(static streamEvent => streamEvent.Sequence).SequenceEqual([3L, 4L]),
            "Last-Event-ID is a watermark, so resuming at a hole's own sequence is as correct as resuming at a published one.");
        AssertEx.True(belowTheHole.Select(static streamEvent => streamEvent.Sequence).SequenceEqual([3L, 4L]));
    }

    /// <summary>Test 7c.</summary>
    [Test]
    public async Task ReadAsync_WithAReservationAboveTheTerminal_StillCompletesOnTheTerminal()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);
        var pending = buffer.Reserve(executionId);

        var events = await DrainAsync(buffer, executionId, sinceSequence: 0);

        AssertEx.Equal(expected: 3L, pending);
        AssertEx.Equal(expected: 3L, buffer.LastSequence(executionId), "The head names a sequence no reader can ever see.");
        AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted,
            events[^1].Type,
            "Completion is decided by the type yielded; a cursor == LastSequence implementation would hang here.");
    }

    /// <summary>Test 7d — the review's N / N+1 scenario. This is the test a barrier-less reader fails.</summary>
    [Test]
    public async Task ReadAsync_WhenALowerSequenceIsStillPending_DeliversItBeforeTheHigherOne()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);

        await using var reader = new Reader(buffer, executionId, sinceSequence: 0);
        await reader.WaitForCountAsync(expected: 1);

        var reserved = buffer.Reserve(executionId);
        var later = Append(buffer, executionId);
        await AssertStaysAtAsync(reader, expected: 1,
            "A reader that yielded N+1 would advance past N and lose the committed event forever, with no gap to report it.");

        buffer.Publish(new IntegrationStreamEvent(IntegrationStreamEventTypes.ExternalOutput,
            reserved,
            executionId,
            Guid.NewGuid(),
            OccurredAtUtc: 1,
            "application/json",
            Payload: null));
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.True(reader.Snapshot().Select(static streamEvent => streamEvent.Sequence).SequenceEqual([1L, reserved, later.Sequence, 4L]),
            "A late publish is delivered in ascending order, so the wire's id: values never go backwards.");
    }

    /// <summary>Test 7e.</summary>
    [Test]
    public async Task ReadAsync_WhenAPendingReservationIsAbandoned_ResumesOnThatWakeupAloneWithNoClock()
    {
        // A ManualTimeProvider that is never advanced: if the barrier were released by a poll or a timeout rather than
        // by the Appended swap, this reader would never move.
        var time = new ManualTimeProvider();
        using var buffer = CreateBuffer(timeProvider: time);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);

        await using var reader = new Reader(buffer, executionId, sinceSequence: 0);
        await reader.WaitForCountAsync(expected: 1);

        var reserved = buffer.Reserve(executionId);
        _ = Append(buffer, executionId);
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);
        await AssertStaysAtAsync(reader, expected: 1);

        buffer.Abandon(executionId, reserved);
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.True(reader.Snapshot().Select(static streamEvent => streamEvent.Sequence).SequenceEqual([1L, 3L, 4L]),
            "Abandon resolves the barrier and completes the same source an append does; the hole stays and the reader proceeds.");
    }

    /// <summary>Test 7f.</summary>
    [Test]
    public async Task ReadAsync_DoesNotCompleteOnATerminalWhileALowerSequenceIsPending()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);

        await using var reader = new Reader(buffer, executionId, sinceSequence: 0);
        await reader.WaitForCountAsync(expected: 1);

        var reserved = buffer.Reserve(executionId);
        var terminal = buffer.Reserve(executionId);
        buffer.Publish(Event(executionId, terminal, IntegrationStreamEventTypes.ExecutionCompleted));
        await AssertStaysAtAsync(reader, expected: 1, "A stream must never end with an earlier committed event still undelivered.");

        buffer.Publish(Event(executionId, reserved, IntegrationStreamEventTypes.ExternalOutput));
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.True(reader.Snapshot().Select(static streamEvent => streamEvent.Sequence).SequenceEqual([1L, reserved, terminal]));
    }

    /// <summary>Test 7f, the abandon arm: the terminal alone.</summary>
    [Test]
    public async Task ReadAsync_WhenTheLowerReservationIsAbandoned_YieldsTheTerminalAlone()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);

        await using var reader = new Reader(buffer, executionId, sinceSequence: 0);
        await reader.WaitForCountAsync(expected: 1);

        var reserved = buffer.Reserve(executionId);
        var terminal = buffer.Reserve(executionId);
        buffer.Publish(Event(executionId, terminal, IntegrationStreamEventTypes.ExecutionCompleted));
        buffer.Abandon(executionId, reserved);
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.True(reader.Snapshot().Select(static streamEvent => streamEvent.Sequence).SequenceEqual([1L, terminal]));
    }

    /// <summary>Test 7g.</summary>
    [Test]
    public async Task ReadAsync_AtOrBelowAPendingReservation_ParksInsteadOfRaisingAGap()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);
        var pending = buffer.Reserve(executionId);

        await using var below = new Reader(buffer, executionId, pending - 1);
        await using var at = new Reader(buffer, executionId, pending);

        await AssertStaysAtAsync(below, expected: 0, "A pending reservation is a wait; reporting it as a gap would 410 healthy first attaches.");
        AssertEx.Equal(expected: 0, at.Snapshot().Count);
        AssertEx.False(below.Completion.IsCompleted);
        AssertEx.False(at.Completion.IsCompleted);
    }

    /// <summary>Test 7h.</summary>
    [Test]
    public async Task ReadAsync_UnderConcurrentReservationsAndAppends_AlwaysReturnsAnAscendingRun()
    {
        using var buffer = CreateBuffer(capacity: 64, maxTracked: 8);
        var random = new Random(Seed: 20260903);
        for (var iteration = 0; iteration < 1_000; iteration++)
        {
            var executionId = Guid.NewGuid();
            AssertEx.True(buffer.TryCreate(executionId));
            _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionAccepted);
            var reserved = buffer.Reserve(executionId);
            var publish = random.Next(maxValue: 2) == 0;

            var reading = DrainAsync(buffer, executionId, sinceSequence: 0);
            var appending = Task.Run(() =>
            {
                _ = Append(buffer, executionId);
                _ = Append(buffer, executionId);
                _ = Append(buffer, executionId);
            });
            var resolving = Task.Run(() =>
            {
                if (publish)
                {
                    buffer.Publish(Event(executionId, reserved, IntegrationStreamEventTypes.ExternalOutput));
                }
                else
                {
                    buffer.Abandon(executionId, reserved);
                }
            });

            await Task.WhenAll(appending, resolving);
            _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);
            var events = await reading;

            var sequences = events.Select(static streamEvent => streamEvent.Sequence).ToArray();
            AssertEx.True(sequences.Zip(sequences.Skip(count: 1)).All(static pair => pair.Second > pair.First),
                $"Iteration {iteration}: every reader observes strictly ascending published sequences.");
            AssertEx.Equal(publish, sequences.Contains(reserved), $"Iteration {iteration}: the reserved sequence appears exactly when it was published.");
            AssertEx.Equal(IntegrationStreamEventTypes.ExecutionCompleted, events[^1].Type, $"Iteration {iteration}: every ReadAsync returns.");
            buffer.Remove(executionId);
        }
    }

    /// <summary>Test 8 — the writer's clean-close input.</summary>
    [Test]
    public async Task ReadAsync_WhenTheEntryIsEvictedMidStream_RaisesTheGapOutOfTheEnumerator()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);

        await using var reader = new Reader(buffer, executionId, sinceSequence: 0);
        await reader.WaitForCountAsync(expected: 1);

        buffer.Remove(executionId);

        var gap = await AssertEx.ThrowsAsync<IntegrationEventGapException>(() => reader.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertEx.Equal(executionId, gap.ExecutionId);
    }

    /// <summary>
    ///     Test 8a — host shutdown. A parked reader waits on its entry's source and nothing else, so disposal has to
    ///     complete those sources: otherwise every open stream waits out its own request token instead of ending here.
    /// </summary>
    [Test]
    public async Task Dispose_WakesAParkedReaderInsteadOfLeavingItOnASourceNoWriterCanReach()
    {
        // Disposed by hand mid-test, so it cannot also be a `using`: this buffer's Dispose cancels its own sweep token
        // and is not written to survive a second call.
        var buffer = CreateBuffer();
        try
        {
            var executionId = Guid.NewGuid();
            AssertEx.True(buffer.TryCreate(executionId));
            _ = Append(buffer, executionId);

            await using var reader = new Reader(buffer, executionId, sinceSequence: 0);
            await reader.WaitForCountAsync(expected: 1);

            buffer.Dispose();
            buffer = null;

            var gap = await AssertEx.ThrowsAsync<IntegrationEventGapException>(() => reader.Completion.WaitAsync(TimeSpan.FromSeconds(10)),
                "A reader parked across disposal must find the entry gone and answer the gap, exactly as it does for Remove.");
            AssertEx.Equal(executionId, gap.ExecutionId);
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    /// <summary>Test 9.</summary>
    [Test]
    public async Task ReadAsync_WhileFourThreadsAppend_YieldsEveryMintedSequenceInOrder()
    {
        using var buffer = CreateBuffer(capacity: 4096);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        var reading = DrainAsync(buffer, executionId, sinceSequence: 0, TimeSpan.FromSeconds(60));
        await Task.WhenAll(Enumerable.Range(start: 0, count: 4)
                                     .Select(worker => Task.Run(() =>
                                     {
                                         for (var index = 0; index < 500; index++)
                                         {
                                             _ = Append(buffer, executionId);
                                         }

                                         return worker;
                                     })));
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);

        var events = await reading;

        var sequences = events.Select(static streamEvent => streamEvent.Sequence).ToArray();
        AssertEx.Equal(expected: 2001, sequences.Length);
        AssertEx.True(sequences.SequenceEqual(Enumerable.Range(start: 1, count: 2001).Select(static value => (long)value)),
            "The wakeup swap under the buffer's lock must lose nothing, however many threads append.");
    }

    private static async Task<List<IntegrationStreamEvent>> DrainAsync(IIntegrationExecutionEventBuffer buffer,
        Guid executionId,
        long sinceSequence,
        TimeSpan? timeout = null)
    {
        // A deadline rather than an open wait: a regression must fail the run, not hang it.
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        var events = new List<IntegrationStreamEvent>();
        await foreach (var streamEvent in buffer.ReadAsync(executionId, sinceSequence, cancellation.Token).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    /// <summary>
    ///     A negative assertion needs a settling window: the reader must be given the chance to yield and then be shown
    ///     not to have. Draining the scheduler is that chance, without a wall-clock guess that only fails when the
    ///     reader gets slower.
    /// </summary>
    private static async Task AssertStaysAtAsync(Reader reader, int expected, string? message = null)
    {
        await AssertEx.SettleAsync();
        AssertEx.Equal(expected, reader.Snapshot().Count, message);
    }

    private static IntegrationStreamEvent Append(IntegrationExecutionEventBuffer buffer,
        Guid executionId,
        string type = IntegrationStreamEventTypes.AssistantDelta) =>
        buffer.Append(executionId, Guid.NewGuid(), type, contentType: null, payload: null);

    private static IntegrationStreamEvent Event(Guid executionId, long sequence, string type) =>
        new(type, sequence, executionId, Guid.NewGuid(), OccurredAtUtc: 1, ContentType: null, Payload: null);

    private static IntegrationExecutionEventBuffer CreateBuffer(int capacity = 2048,
        int maxBytes = 4 * 1024 * 1024,
        int maxTracked = 64,
        TimeProvider? timeProvider = null) =>
        new(Options.Create(new IntegrationOptions
            {
                EventBufferCapacity = capacity,
                EventBufferMaxBytes = maxBytes,
                MaxTrackedExecutions = maxTracked,
                EventBufferTtlAfterTerminal = TimeSpan.FromMinutes(10)
            }),
            timeProvider ?? TimeProvider.System);

    /// <summary>A reader running on its own task, so a test can assert what it has and has NOT yielded so far.</summary>
    private sealed class Reader : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly List<IntegrationStreamEvent> _events = [];
        private readonly Lock _gate = new();

        public Reader(IIntegrationExecutionEventBuffer buffer, Guid executionId, long sinceSequence) =>
            Completion = Task.Run(async () =>
            {
                await foreach (var streamEvent in buffer.ReadAsync(executionId, sinceSequence, _cancellation.Token).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        _events.Add(streamEvent);
                    }
                }
            },
                _cancellation.Token);

        public Task Completion { get; }

        public IReadOnlyList<IntegrationStreamEvent> Snapshot()
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }

        public Task WaitForCountAsync(int expected) =>
            AssertEx.EventuallyAsync(() => Snapshot().Count >= expected, TimeSpan.FromSeconds(10), $"The reader never reached {expected} events.");

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            try
            {
                await Completion;
            }
            catch (OperationCanceledException)
            {
                // The test's own shutdown signal.
            }
            catch (IntegrationEventGapException)
            {
                // Asserted by whichever test arranged it.
            }

            _cancellation.Dispose();
        }
    }
}
