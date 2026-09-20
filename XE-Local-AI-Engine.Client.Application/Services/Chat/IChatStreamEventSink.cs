namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The write surface every producer of one turn's SSE events shares: the invocation-state pump, the run task and
///     the four dispatcher event handlers.
/// </summary>
/// <remarks>
///     It stands in for a raw <c>ChannelWriter&lt;ChatStreamEvent&gt;</c> so the queue can be BOUNDED — a
///     disconnected browser otherwise leaves every producer writing with no reader for the rest of the run — without
///     each call site learning how the bound is enforced. Two rules callers depend on: a write NEVER blocks, because
///     the pump owns persistence and stalling it would stall the run's terminal, and a write never throws once
///     <see cref="Detach" /> has run.
/// </remarks>
public interface IChatStreamEventSink
{
    /// <summary>Enqueues an event from an awaitable producer, preserving call order. Never waits for capacity.</summary>
    ValueTask WriteAsync(ChatStreamEvent streamEvent, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Enqueues an event from a synchronous producer, returning <c>false</c> when the queue refused it outright.
    /// </summary>
    /// <remarks>Every refusal is accounted for internally, so callers may ignore the result and none act on it.</remarks>
    bool TryWrite(ChatStreamEvent streamEvent);

    /// <summary>Drains the buffered events in order until <see cref="Complete" /> and the queue are both exhausted.</summary>
    IAsyncEnumerable<ChatStreamEvent> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The SSE consumer is gone, so subsequent writes become no-ops.
    /// </summary>
    /// <remarks>
    ///     It must NOT complete the queue: the pump treats a write fault as a persistence fault and would terminalize
    ///     the row <c>Failed</c>, while the run deliberately continues, with the persisted row and the resume registry
    ///     as the recovery surface, exactly as for a reload.
    /// </remarks>
    void Detach();

    /// <summary>Signals end of stream. Buffered events still drain to a reader that is still attached.</summary>
    void Complete();
}
