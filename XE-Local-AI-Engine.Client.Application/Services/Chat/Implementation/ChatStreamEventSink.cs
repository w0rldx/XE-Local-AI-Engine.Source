namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     The bounded event queue for one streaming turn: six concurrent producers, one consumer draining it, bounded on
///     two axes and never making a producer wait.
/// </summary>
/// <remarks>
///     It is not unbounded because on a disconnect the SSE loop exits while every producer writes on;
///     <see cref="Detach" /> turns those writes into no-ops and the bound caps what a merely SLOW consumer holds. It
///     does not <see cref="BoundedChannelFullMode.Wait" /> because the pump both writes here and owns persistence, so
///     a blocked write would stall the run's terminal. It drops instead and repairs at the STREAM level, with one
///     <c>assistant-reconcile</c>: no per-kind policy is safe, since a turn missing an approval cannot render.
/// </remarks>
public sealed class ChatStreamEventSink : IChatStreamEventSink
{
    private const string DroppedByCapacity = "queue_capacity";
    private const string DroppedByBytes = "queue_bytes";

    private readonly Channel<ChatStreamEvent> _channel;
    private readonly NodeChatMessageCorrelation _correlation;
    private readonly int _maxQueuedChars;
    private readonly NodeChatStreamSequence _sequence;
    private readonly TimeProvider _timeProvider;

    // Set when an enqueue was refused, cleared by the reader when it emits the reconcile. An int, not a bool, so the
    // read-and-clear is atomic and one burst of drops yields exactly one reconcile without locking the write path.
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
        while (await _channel.Reader.WaitToReadAsync(cancellationToken))
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
    ///     The characters one event contributes to the queue's memory footprint.
    /// </summary>
    /// <remarks>
    ///     Only the fields that can be large are counted: a tool result or an argument blob is how a bounded-by-COUNT
    ///     queue still holds hundreds of megabytes, and the ids and status strings are noise beside them.
    /// </remarks>
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
