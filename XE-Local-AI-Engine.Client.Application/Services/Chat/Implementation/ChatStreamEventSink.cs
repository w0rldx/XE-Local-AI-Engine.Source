namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     The bounded event queue for one streaming turn. Six producers write it concurrently and exactly one consumer
///     (the SSE loop) drains it, so it is a <see cref="Channel{T}" /> underneath — but a BOUNDED one, on two axes, and
///     one that never makes a producer wait.
///     <para>
///         Why not unbounded: on client disconnect the SSE <c>await foreach</c> exits while every producer keeps
///         writing, with no reader, for the rest of the run. Every event was retained until the iterator finished.
///         <see cref="Detach" /> closes that by turning writes into no-ops, and the bound caps what a merely-SLOW
///         consumer can accumulate before then.
///     </para>
///     <para>
///         Why not <see cref="BoundedChannelFullMode.Wait" />: the pump both writes here and owns persistence, so
///         blocking a write on a full queue would stall the database writes the run's real terminal depends on. The
///         queue therefore drops, and a drop is repaired at the STREAM level, not the event level: one
///         <c>assistant-reconcile</c> tells the client to re-subscribe through <c>ResumeMessage</c>, whose first frame
///         is an authoritative snapshot. Dropping selectively would risk silently losing an approval — and there is no
///         per-kind policy that can be safely written, because the client cannot render a turn that is missing one.
///     </para>
///     <para>
///         Deltas are coalesced at the PRODUCER (the pump's emit debounce), never here, so a coalesced delta consumes
///         exactly one sequence number and the client's ordering guard never waits on a hole. What is dropped here has
///         already consumed a sequence — which is precisely why a drop must reconcile rather than pass unremarked.
///     </para>
/// </summary>
public sealed class ChatStreamEventSink : IChatStreamEventSink
{
    private const string DroppedByCapacity = "queue_capacity";
    private const string DroppedByBytes = "queue_bytes";

    private readonly Channel<ChatStreamEvent> _channel;
    private readonly NodeChatMessageCorrelation _correlation;
    private readonly int _maxQueuedChars;
    private readonly NodeChatStreamSequence _sequence;
    private readonly TimeProvider _timeProvider;

    // Set when an enqueue was refused for either reason; cleared by the reader when it emits the reconcile. An int
    // rather than a bool so the read-and-clear is one atomic operation, which is what makes "exactly one reconcile per
    // burst of drops" true without a lock on the write path.
    private int _reconcileNeeded;

    private long _queuedChars;
    private volatile bool _detached;

    public ChatStreamEventSink(NodeChatMessageCorrelation correlation,
        NodeChatStreamSequence sequence,
        ChatStreamBudgetOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);

        _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        _sequence = sequence ?? throw new ArgumentNullException(nameof(sequence));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _maxQueuedChars = options.MaxQueuedChars;

        _channel = Channel.CreateBounded<ChatStreamEvent>(new BoundedChannelOptions(options.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite
            },
            // TryWrite reports SUCCESS for a DropWrite drop, so this callback is the only place the count overflow is
            // observable. It runs synchronously on the writing thread.
            OnDroppedByCapacity);
    }

    public ValueTask WriteAsync(ChatStreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        // Detached first, and before the cancellation check: a detached write must never throw, and the SSE loop's
        // teardown runs while the run's own token is still live anyway.
        if (_detached)
        {
            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        TryWrite(streamEvent);

        // Always synchronous: DropWrite means the queue never asks a producer to wait, which is the property the pump
        // depends on to keep persisting while a consumer lags.
        return ValueTask.CompletedTask;
    }

    public bool TryWrite(ChatStreamEvent streamEvent)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        if (_detached)
        {
            return true;
        }

        var cost = CharCost(streamEvent);
        if (Interlocked.Add(ref _queuedChars, cost) > _maxQueuedChars)
        {
            Interlocked.Add(ref _queuedChars, -cost);
            LatchReconcile(DroppedByBytes);
            return false;
        }

        // From here the channel owns the accounting: either the item is buffered (and the reader decrements on
        // dequeue) or OnDroppedByCapacity decrements it for us.
        return _channel.Writer.TryWrite(streamEvent);
    }

    public async IAsyncEnumerable<ChatStreamEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out var streamEvent))
            {
                if (TryConsumeReconcile())
                {
                    yield return ReconcileEvent();
                }

                Interlocked.Add(ref _queuedChars, -CharCost(streamEvent));
                yield return streamEvent;
            }
        }

        // A drop that happened after the last buffered item still has to be told: without this the client would keep
        // rendering a turn that silently lost a frame until the terminal converged it.
        if (TryConsumeReconcile())
        {
            yield return ReconcileEvent();
        }
    }

    public void Detach()
    {
        _detached = true;
    }

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }

    /// <summary>
    ///     The characters one event contributes to the queue's memory footprint. Only the fields that can be large are
    ///     counted — a tool result or an argument blob is the realistic way a bounded-by-COUNT queue still holds
    ///     hundreds of megabytes; the correlation ids and status strings are noise beside them.
    /// </summary>
    private static int CharCost(ChatStreamEvent streamEvent)
    {
        return (streamEvent.Delta?.Length ?? 0)
               + (streamEvent.ReasoningDelta?.Length ?? 0)
               + (streamEvent.Content?.Length ?? 0)
               + (streamEvent.Reasoning?.Length ?? 0)
               + (streamEvent.Arguments?.Length ?? 0)
               + (streamEvent.Result?.Length ?? 0)
               + (streamEvent.Questions?.Length ?? 0);
    }

    private void OnDroppedByCapacity(ChatStreamEvent dropped)
    {
        Interlocked.Add(ref _queuedChars, -CharCost(dropped));
        LatchReconcile(DroppedByCapacity);
    }

    private void LatchReconcile(string reason)
    {
        NodeMetrics.ChatStreamEnqueueDroppedTotal.Add(1, new KeyValuePair<string, object?>("reason", reason));
        Interlocked.Exchange(ref _reconcileNeeded, 1);
    }

    private bool TryConsumeReconcile()
    {
        return Interlocked.Exchange(ref _reconcileNeeded, value: 0) == 1;
    }

    private ChatStreamEvent ReconcileEvent()
    {
        NodeMetrics.ChatStreamReconcileTotal.Add(1, new KeyValuePair<string, object?>("reason", "queue_overflow"));

        return ChatStreamEventMapper.ReconcileEvent(_correlation,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            _sequence.Next());
    }
}
