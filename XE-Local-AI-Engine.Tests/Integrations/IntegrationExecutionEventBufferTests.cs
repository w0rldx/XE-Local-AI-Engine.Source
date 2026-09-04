namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The event ring: the only minter of a sequence in this feature. Two properties carry the correctness argument.
///     A reserved-but-unresolved sequence is a BARRIER — a reader must not run past it, because a late publish would
///     otherwise fall below the cursor and be lost to every live consumer forever. And an entry with a pending
///     reservation is pinned: evicting it would strand a reader and let the publish that follows land on nothing.
/// </summary>
public sealed class IntegrationExecutionEventBufferTests
{
    [Test]
    public void Append_MintsOneTwoThreePerExecutionAndKeepsExecutionsIndependent()
    {
        using var buffer = CreateBuffer();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(first));
        AssertEx.True(buffer.TryCreate(second));

        AssertEx.Equal(expected: 1L, Append(buffer, first).Sequence);
        AssertEx.Equal(expected: 2L, Append(buffer, first).Sequence);
        AssertEx.Equal(expected: 3L, Append(buffer, first).Sequence);
        AssertEx.Equal(expected: 1L, Append(buffer, second).Sequence);

        AssertEx.Equal(expected: 3L, buffer.LastSequence(first));
        AssertEx.Equal(expected: 1L, buffer.LastSequence(second));
    }

    [Test]
    public void TryCreate_WithAnInitialSequence_ContinuesTheRowsOwnNumbering()
    {
        // The startup sweep's path: a recovered execution's terminal event must continue where the persisted rows
        // stopped, not restart at 1 and collide with them.
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();

        AssertEx.True(buffer.TryCreate(executionId, initialSequence: 41));

        AssertEx.Equal(expected: 42L, Append(buffer, executionId).Sequence);
    }

    [Test]
    public void TryCreate_IsIdempotentAndKeepsTheCounter()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);

        AssertEx.True(buffer.TryCreate(executionId, initialSequence: 0));

        AssertEx.Equal(expected: 2L, Append(buffer, executionId).Sequence, "A second TryCreate must not reset a live counter.");
    }

    [Test]
    public void IsTracked_DistinguishesAnUntrackedIdFromOneSittingAtZero()
    {
        // The numbers alone cannot: an untracked id and a tracked id with nothing appended both read (floor 0,
        // head 0), and the stream writer has to answer 404 for one and 410 for the other.
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();

        AssertEx.False(buffer.IsTracked(executionId));
        AssertEx.True(buffer.TryCreate(executionId));
        AssertEx.True(buffer.IsTracked(executionId));
        AssertEx.Equal(expected: 0L, buffer.Floor(executionId));
        AssertEx.Equal(expected: 0L, buffer.LastSequence(executionId));

        buffer.Remove(executionId);
        AssertEx.False(buffer.IsTracked(executionId));
    }

    [Test]
    public void FloorAndLastSequence_ReturnZeroForAnUntrackedId()
    {
        using var buffer = CreateBuffer();

        AssertEx.Equal(expected: 0L, buffer.Floor(Guid.NewGuid()));
        AssertEx.Equal(expected: 0L, buffer.LastSequence(Guid.NewGuid()));
    }

    [Test]
    public void Append_OnAnUntrackedId_Throws()
    {
        // Loud, because silently creating an entry would restart the counter at 1 and mint sequences that collide with
        // the ones already persisted for that execution.
        using var buffer = CreateBuffer();

        _ = AssertEx.Throws<InvalidOperationException>(() => Append(buffer, Guid.NewGuid()));
        _ = AssertEx.Throws<InvalidOperationException>(() => buffer.Reserve(Guid.NewGuid()));
    }

    [Test]
    public void Append_PastTheCountCap_TrimsTheOldestAndRaisesTheFloor()
    {
        using var buffer = CreateBuffer(capacity: 4);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        for (var i = 0; i < 10; i++)
        {
            _ = Append(buffer, executionId);
        }

        AssertEx.Equal(expected: 7L, buffer.Floor(executionId), "Four retained events out of ten leaves 7..10.");
        AssertEx.Equal(expected: 10L, buffer.LastSequence(executionId));
    }

    [Test]
    public void Append_OfAnEventLargerThanTheWholeByteCap_StillReportsTheGapItLeft()
    {
        // The configurable shape: EventBufferMaxBytes allows 64 KiB while one external.output may be 256 KiB. Trimming
        // to EMPTY once read the floor off an absent head and answered 0, which a reader takes for "no gap" — so it
        // would skip the 410 and silently miss every dropped event.
        using var buffer = CreateBuffer(maxBytes: 10);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        for (var i = 0; i < 3; i++)
        {
            _ = Append(buffer, executionId);
        }

        AssertEx.Equal(expected: 4L, buffer.Floor(executionId), "Three dropped events leave the floor at the highest dropped sequence + 1.");
        AssertEx.Equal(expected: 3L, buffer.LastSequence(executionId), "The minting watermark is untouched by trimming.");
    }

    [Test]
    public void Append_PastTheByteCap_TrimsEvenWhenTheCountIsFarBelowTheLimit()
    {
        using var buffer = CreateBuffer(capacity: 4096, maxBytes: 700);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        for (var i = 0; i < 20; i++)
        {
            _ = Append(buffer, executionId);
        }

        AssertEx.True(buffer.Floor(executionId) > 1, "The byte ceiling must bite before the count ceiling does.");
    }

    [Test]
    public void Append_ClonesThePayload_SoADisposedDocumentIsStillReadable()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        IntegrationStreamEvent appended;
        using (var document = JsonDocument.Parse("""{"name":"probe"}"""))
        {
            appended = buffer.Append(executionId,
                Guid.NewGuid(),
                IntegrationStreamEventTypes.ToolStarted,
                contentType: null,
                document.RootElement);
        }

        // Without the clone this throws ObjectDisposedException from inside whoever reads the stream, slices later and
        // on a different thread.
        AssertEx.True(appended.Payload is not null, "The buffered event must keep a payload of its own.");
        AssertEx.Equal("probe", appended.Payload!.Value.GetProperty("name").GetString());
    }

    [Test]
    public void TryCreate_UnderPressure_EvictsTheOldestTerminalEntryAndNeverALiveOne()
    {
        using var buffer = CreateBuffer(maxTracked: 2);
        var terminal = Guid.NewGuid();
        var live = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(terminal));
        AssertEx.True(buffer.TryCreate(live));
        _ = Append(buffer, terminal, IntegrationStreamEventTypes.ExecutionCompleted);
        _ = Append(buffer, live);

        AssertEx.True(buffer.TryCreate(Guid.NewGuid()), "A terminal entry is evictable, so the ring makes room.");

        AssertEx.False(buffer.IsTracked(terminal));
        AssertEx.True(buffer.IsTracked(live), "A live execution is never evicted: a reader attached to it would get a gap for a run still producing.");
    }

    [Test]
    public void TryCreate_WhenEveryTrackedEntryIsLive_ReturnsFalse()
    {
        using var buffer = CreateBuffer(maxTracked: 2);
        AssertEx.True(buffer.TryCreate(Guid.NewGuid()));
        AssertEx.True(buffer.TryCreate(Guid.NewGuid()));

        AssertEx.False(buffer.TryCreate(Guid.NewGuid()), "With nothing evictable the accept path answers 503 rather than dropping a live stream.");
    }

    [Test]
    public void Remove_FreesATrackedSlot()
    {
        using var buffer = CreateBuffer(maxTracked: 1);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        AssertEx.False(buffer.TryCreate(Guid.NewGuid()));

        buffer.Remove(executionId);

        AssertEx.True(buffer.TryCreate(Guid.NewGuid()));
    }

    [Test]
    public void Sweep_DropsATerminalEntryPastTheTtlAndKeepsALiveOne()
    {
        var clock = new ManualTimeProvider();
        using var buffer = CreateBuffer(ttl: TimeSpan.FromMinutes(10), timeProvider: clock);
        var terminal = Guid.NewGuid();
        var live = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(terminal));
        AssertEx.True(buffer.TryCreate(live));
        _ = Append(buffer, terminal, IntegrationStreamEventTypes.ExecutionCancelled);
        _ = Append(buffer, live);

        clock.Advance(TimeSpan.FromMinutes(11));
        _ = buffer.Sweep();

        AssertEx.False(buffer.IsTracked(terminal));
        AssertEx.True(buffer.IsTracked(live), "The option's name is the contract: only a TERMINAL entry ages out.");
    }

    [Test]
    public void RemoveAndSweep_DiscardTheEvictionQueueEntryAsWellAsTheEntry()
    {
        // The FIFO only shed stale ids when an eviction scan happened to run, so a node that never reaches
        // MaxTrackedExecutions grew it without bound for the whole life of the process.
        var clock = new ManualTimeProvider();
        using var buffer = CreateBuffer(ttl: TimeSpan.FromMinutes(10), timeProvider: clock);

        for (var i = 0; i < 20; i++)
        {
            var removed = Guid.NewGuid();
            AssertEx.True(buffer.TryCreate(removed));
            _ = Append(buffer, removed, IntegrationStreamEventTypes.ExecutionCompleted);
            buffer.Remove(removed);
        }

        var swept = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(swept));
        _ = Append(buffer, swept, IntegrationStreamEventTypes.ExecutionCompleted);
        clock.Advance(TimeSpan.FromMinutes(11));
        _ = buffer.Sweep();

        AssertEx.Equal(expected: 0, buffer.EvictionQueueDepth, "Every id the FIFO names must have an entry behind it.");
    }

    [Test]
    public void Reserve_AdvancesTheMintWatermarkButPublishesNothing()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        var parked = buffer.AppendedTask(executionId);

        var reserved = buffer.Reserve(executionId);

        AssertEx.Equal(expected: 1L, reserved);
        AssertEx.Equal(expected: 1L, buffer.LastSequence(executionId), "LastSequence is a MINTING watermark, not a 'highest readable' claim.");
        AssertEx.Equal(expected: 0L, buffer.Floor(executionId), "Nothing is readable until the commit lands and Publish runs.");
        AssertEx.False(parked.IsCompleted, "A reservation wakes nobody.");
    }

    [Test]
    public void Publish_MakesAReservedEventReadableInSequenceOrderEvenWhenAnAppendMintedAHigherOne()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        var reserved = buffer.Reserve(executionId);
        var later = Append(buffer, executionId);
        AssertEx.Equal(expected: 2L, later.Sequence);

        buffer.Publish(Event(executionId, reserved, IntegrationStreamEventTypes.ExecutionCompleted));

        AssertEx.Equal(expected: 1L, buffer.Floor(executionId), "The late publish must sort BELOW the append that overtook it, not append after it.");
    }

    [Test]
    public void Publish_OfAnUnreservedOrAlreadyPublishedSequence_Throws()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        var reserved = buffer.Reserve(executionId);
        buffer.Publish(Event(executionId, reserved, IntegrationStreamEventTypes.ExecutionFailed));

        _ = AssertEx.Throws<InvalidOperationException>(() => buffer.Publish(Event(executionId, reserved, IntegrationStreamEventTypes.ExecutionFailed)));
        _ = AssertEx.Throws<InvalidOperationException>(() => buffer.Publish(Event(executionId, sequence: 99, IntegrationStreamEventTypes.ExecutionFailed)));
    }

    [Test]
    public void Abandon_LeavesAHoleAndThrowsWhenTheSequenceWasNeverReservedOrIsAlreadyResolved()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId);
        var reserved = buffer.Reserve(executionId);
        var after = Append(buffer, executionId);

        buffer.Abandon(executionId, reserved);

        AssertEx.Equal(expected: 3L, after.Sequence);
        AssertEx.Equal(expected: 1L, buffer.Floor(executionId), "The ring reads 1, 3 — a hole is legal, and Last-Event-ID is a watermark rather than a contiguity claim.");
        _ = AssertEx.Throws<InvalidOperationException>(() => buffer.Abandon(executionId, reserved));
        _ = AssertEx.Throws<InvalidOperationException>(() => buffer.Abandon(executionId, sequence: 42));
    }

    [Test]
    public void LowestPendingReservation_IsTheBarrierAndClearsOnEitherResolution()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();

        AssertEx.Equal(long.MaxValue, buffer.LowestPendingReservation(executionId), "An untracked id must read as 'no barrier'.");
        AssertEx.True(buffer.TryCreate(executionId));
        AssertEx.Equal(long.MaxValue, buffer.LowestPendingReservation(executionId));

        var first = buffer.Reserve(executionId);
        var second = buffer.Reserve(executionId);
        AssertEx.Equal(first, buffer.LowestPendingReservation(executionId), "With two outstanding, the barrier is the LOWER one.");

        buffer.Publish(Event(executionId, first, IntegrationStreamEventTypes.ExternalOutput));
        AssertEx.Equal(second, buffer.LowestPendingReservation(executionId));

        buffer.Abandon(executionId, second);
        AssertEx.Equal(long.MaxValue, buffer.LowestPendingReservation(executionId), "Abandon resolves the barrier exactly as Publish does.");
    }

    [Test]
    public void TheRoundFiveScenario_APublishThatLandsLateIsStillDeliveredBehindTheBarrier()
    {
        // Reserve N, append N+1: a reader that yielded N+1 would advance its cursor past N, and N's later publish would
        // fall below that cursor and never be delivered. The barrier is what makes "lost forever" into "slightly late".
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        var reserved = buffer.Reserve(executionId);
        var overtaking = Append(buffer, executionId);

        AssertEx.Equal(reserved, buffer.LowestPendingReservation(executionId));
        AssertEx.True(overtaking.Sequence > buffer.LowestPendingReservation(executionId),
            "The overtaking event sits AT OR ABOVE the barrier, so a reader honouring it cannot yield yet.");

        buffer.Publish(Event(executionId, reserved, IntegrationStreamEventTypes.ExternalOutput));

        AssertEx.Equal(long.MaxValue, buffer.LowestPendingReservation(executionId));
        AssertEx.Equal(reserved, buffer.Floor(executionId), "Both events are now readable, in order.");
    }

    [Test]
    public async Task PublishAndAbandon_BothCompleteTheWakeupSourceAReaderIsParkedOn()
    {
        using var buffer = CreateBuffer();
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        var beforePublish = buffer.AppendedTask(executionId);
        var published = buffer.Reserve(executionId);
        buffer.Publish(Event(executionId, published, IntegrationStreamEventTypes.ExternalOutput));
        await beforePublish.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        var beforeAbandon = buffer.AppendedTask(executionId);
        var abandoned = buffer.Reserve(executionId);
        buffer.Abandon(executionId, abandoned);
        await beforeAbandon.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    [Test]
    public void AnEntryWithAPendingReservation_IsNeitherEvictedNorSwept()
    {
        var clock = new ManualTimeProvider();
        using var buffer = CreateBuffer(maxTracked: 1, ttl: TimeSpan.FromMinutes(10), timeProvider: clock);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));
        _ = Append(buffer, executionId, IntegrationStreamEventTypes.ExecutionCompleted);
        var reserved = buffer.Reserve(executionId);

        clock.Advance(TimeSpan.FromMinutes(11));

        _ = buffer.Sweep();
        AssertEx.True(buffer.IsTracked(executionId), "A terminal entry still resolving a reservation must survive the TTL.");
        AssertEx.False(buffer.TryCreate(Guid.NewGuid()), "And must not be evicted under tracked-execution pressure either.");

        buffer.Abandon(executionId, reserved);
        _ = buffer.Sweep();
        AssertEx.False(buffer.IsTracked(executionId), "Once the reservation resolves, the ordinary rules apply again.");
    }

    [Test]
    public async Task Append_FromManyThreads_YieldsAContiguousRunWithNoDuplicates()
    {
        using var buffer = CreateBuffer(capacity: 4096);
        var executionId = Guid.NewGuid();
        AssertEx.True(buffer.TryCreate(executionId));

        var minted = await Task.WhenAll(Enumerable.Range(start: 0, count: 8)
                                                  .Select(_ => Task.Run(() => Enumerable.Range(start: 0, count: 50)
                                                                              .Select(_ => Append(buffer, executionId).Sequence)
                                                                              .ToArray())))
                               .ConfigureAwait(false);

        var sequences = minted.SelectMany(static batch => batch).OrderBy(static sequence => sequence).ToArray();
        AssertEx.Equal(expected: 400, sequences.Length);
        AssertEx.True(sequences.SequenceEqual(Enumerable.Range(start: 1, count: 400).Select(static value => (long)value)),
            "The dispatcher raises events from more than one path, so 'one writer thread' is not an assumption this ring may make.");
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
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null) =>
        new(Options.Create(new IntegrationOptions
            {
                EventBufferCapacity = capacity,
                EventBufferMaxBytes = maxBytes,
                MaxTrackedExecutions = maxTracked,
                EventBufferTtlAfterTerminal = ttl ?? TimeSpan.FromMinutes(10)
            }),
            timeProvider ?? TimeProvider.System);
}
