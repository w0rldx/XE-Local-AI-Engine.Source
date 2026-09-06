namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The per-turn event queue is bounded on two axes and never makes a producer wait. These tests pin what that
///     costs and what it must never cost: an overflow drops and resynchronizes the WHOLE stream (never a silent
///     per-event drop, which could lose an approval the turn is parked on), and <c>Detach</c> — the fix for a
///     disconnected browser leaving six producers writing into a queue nobody reads — makes writes inert WITHOUT
///     completing the queue, because the pump reads a write fault as a persistence fault and would terminalize the
///     assistant row Failed.
/// </summary>
public sealed class ChatStreamEventSinkTests
{
    private static readonly NodeChatMessageCorrelation Correlation = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Test]
    public async Task TryWrite_PastQueueCapacity_DropsTheEventAndLatchesAReconcile()
    {
        var sink = NewSink(new ChatStreamBudgetOptions
        {
            QueueCapacity = 2
        });

        sink.TryWrite(Delta("a"));
        sink.TryWrite(Delta("b"));
        sink.TryWrite(Delta("dropped"));
        sink.Complete();

        var events = await DrainAsync(sink).ConfigureAwait(false);

        // The dropped frame is gone and is NOT replaced by a partial: the repair is a re-subscribe, whose first frame
        // is an authoritative snapshot. Anything else would have to invent a merge the client cannot perform.
        AssertEx.True(events.All(streamEvent => streamEvent.Delta != "dropped"), "The refused event must not appear in the stream.");
        AssertEx.ContainsSingle(events, streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantReconcile);

        // The reconcile leads: the client must be told to resynchronize before it renders more of a stream that has
        // already lost a frame.
        AssertEx.Equal(ChatStreamEventTypes.AssistantReconcile, events[0].Type);
        AssertEx.Equal("a", events[1].Delta);
        AssertEx.Equal("b", events[2].Delta);
    }

    [Test]
    public async Task TryWrite_PastMaxQueuedChars_DropsTheEventAndLatchesAReconcile()
    {
        // A count cap alone is not a memory bound — one tool result can be megabytes — so the character cap has to
        // refuse independently of how few events are queued.
        var sink = NewSink(new ChatStreamBudgetOptions
        {
            QueueCapacity = 1024,
            MaxQueuedChars = 10
        });

        AssertEx.True(sink.TryWrite(Delta("12345678")), "A write inside the character budget must be accepted.");
        AssertEx.False(sink.TryWrite(Delta("12345678")), "A write that would exceed the character budget must be refused.");
        sink.Complete();

        var events = await DrainAsync(sink).ConfigureAwait(false);

        AssertEx.ContainsSingle(events, streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantReconcile);
        AssertEx.ContainsSingle(events, streamEvent => streamEvent.Delta == "12345678");
    }

    [Test]
    public async Task ReadAllAsync_ReleasesTheCharacterBudgetAsItDrains()
    {
        // The budget is what is QUEUED, not what has ever been written: without the decrement on dequeue a long turn
        // would reconcile once and then permanently, however fast the consumer read.
        var sink = NewSink(new ChatStreamBudgetOptions
        {
            QueueCapacity = 1024,
            MaxQueuedChars = 10
        });

        AssertEx.True(sink.TryWrite(Delta("12345678")), "A write inside the character budget must be accepted.");

        var events = new List<ChatStreamEvent>();
        var drain = Task.Run(async () =>
        {
            await foreach (var streamEvent in sink.ReadAllAsync())
            {
                events.Add(streamEvent);
            }
        });

        await AssertEx.EventuallyAsync(() => events.Count == 1, TimeSpan.FromSeconds(5));
        AssertEx.True(sink.TryWrite(Delta("87654321")), "Once the first event is drained its characters must be back in the budget.");

        sink.Complete();
        await drain.ConfigureAwait(false);

        AssertEx.Equal(expected: 2, events.Count);
        AssertEx.True(events.All(streamEvent => streamEvent.Type != ChatStreamEventTypes.AssistantReconcile), "Nothing was dropped, so nothing may reconcile.");
    }

    [Test]
    public async Task ReadAllAsync_EmitsExactlyOneReconcilePerBurstOfDrops()
    {
        // Reconciling per dropped EVENT would make a lagging consumer re-resume once per frame, which is a worse
        // stall than the one it is recovering from. The latch collapses a burst into one repair.
        var sink = NewSink(new ChatStreamBudgetOptions
        {
            QueueCapacity = 1
        });

        sink.TryWrite(Delta("kept"));
        sink.TryWrite(Delta("dropped-1"));
        sink.TryWrite(Delta("dropped-2"));
        sink.TryWrite(Delta("dropped-3"));
        sink.Complete();

        var events = await DrainAsync(sink).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, events.Count);
        AssertEx.Equal(ChatStreamEventTypes.AssistantReconcile, events[0].Type);
        AssertEx.Equal("kept", events[1].Delta);
    }

    [Test]
    public async Task ReadAllAsync_WhenNothingIsQueuedBehindTheDrop_StillReconciles()
    {
        // The latch is checked once more after the queue drains. An event refused on the character cap needs no
        // backlog at all — one oversized tool result does it on an empty queue — so without the trailing check the
        // client would keep rendering a stream it does not know lost a frame.
        var sink = NewSink(new ChatStreamBudgetOptions
        {
            QueueCapacity = 1024,
            MaxQueuedChars = 4
        });

        AssertEx.False(sink.TryWrite(Delta("oversized")), "An event larger than the whole character budget must be refused.");
        sink.Complete();

        var events = await DrainAsync(sink).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, events.Count);
        AssertEx.Equal(ChatStreamEventTypes.AssistantReconcile, events[0].Type);
    }

    [Test]
    public async Task Detach_MakesWritesInertWithoutCompletingTheQueue()
    {
        var sink = NewSink(new ChatStreamBudgetOptions());
        sink.Detach();

        // Every producer keeps writing after the browser goes away — the run is deliberately still going. None of
        // them may fault: the pump treats a write exception as a persistence fault and terminalizes the row Failed.
        AssertEx.True(sink.TryWrite(Delta("after-detach")), "A detached write must report success rather than fail its producer.");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await sink.WriteAsync(Delta("after-detach"), cancelled.Token).ConfigureAwait(false);

        // And the queue itself must NOT be completed — a ChannelClosedException reaching the pump reads exactly like
        // a persistence fault, which is the bug this ordering exists to prevent.
        using var readCancellation = new CancellationTokenSource();
        var drain = Task.Run(async () =>
        {
            await foreach (var _ in sink.ReadAllAsync(readCancellation.Token))
            {
                // Nothing is expected: detached writes were discarded and the queue is still open.
            }
        });

        await AssertEx.StaysIncompleteAsync(drain,
                          "Detach must leave the queue open; completing it would surface as a persistence fault in the pump.")
                      .ConfigureAwait(false);

        // Complete is the only thing that ends the stream, and it still does after a detach.
        sink.Complete();
        await AssertEx.CompletesAsync(drain, TestBudgets.Contended, "Complete must end the stream even after a detach.").ConfigureAwait(false);
    }

    [Test]
    public async Task Complete_StillDrainsTheEventsAlreadyBuffered()
    {
        // The SSE loop is often still draining when the pump writes its terminal and completes: Complete means "no
        // more writes", never "discard what is queued".
        var sink = NewSink(new ChatStreamBudgetOptions());

        sink.TryWrite(Delta("one"));
        sink.TryWrite(Delta("two"));
        sink.Complete();

        var events = await DrainAsync(sink).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, events.Count);
        AssertEx.Equal("one", events[0].Delta);
        AssertEx.Equal("two", events[1].Delta);
    }

    private static ChatStreamEventSink NewSink(ChatStreamBudgetOptions options)
    {
        return new ChatStreamEventSink(Correlation, new NodeChatStreamSequence(), options, TimeProvider.System);
    }

    private static ChatStreamEvent Delta(string delta)
    {
        return new ChatStreamEvent(ChatStreamEventTypes.AssistantDelta,
            Correlation.ConversationId,
            Correlation.MessageId,
            Correlation.RequestId,
            NodeChatMessageStatusValues.Streaming,
            Sequence: 0,
            OccurredAtUtc: 0,
            Delta: delta);
    }

    private static async Task<List<ChatStreamEvent>> DrainAsync(ChatStreamEventSink sink)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in sink.ReadAllAsync().ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        return events;
    }
}
