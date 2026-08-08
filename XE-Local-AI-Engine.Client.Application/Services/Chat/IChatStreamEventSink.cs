namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The write surface every producer of one turn's SSE events shares — the invocation-state pump, the run task, and
///     the four dispatcher event handlers. It replaces the raw <c>ChannelWriter&lt;ChatStreamEvent&gt;</c> those
///     producers used to hold, so the queue can be BOUNDED (a disconnected browser previously left every producer
///     writing into an unbounded channel with no reader for the rest of the run) without every call site learning how
///     the bound is enforced.
///     <para>
///         Two rules the implementation must keep, because callers depend on them: a write NEVER blocks (the pump owns
///         persistence, so stalling it on a slow consumer would stall the database writes the run's terminal depends
///         on), and a write never throws once <see cref="Detach" /> has run.
///     </para>
/// </summary>
public interface IChatStreamEventSink
{
    /// <summary>Enqueues an event from an awaitable producer, preserving call order. Never waits for capacity.</summary>
    ValueTask WriteAsync(ChatStreamEvent streamEvent, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Enqueues an event from a synchronous producer (the dispatcher event handlers). Returns <c>false</c> when the
    ///     queue refused it outright; every refusal is already accounted for internally (the stream reconciles), so
    ///     callers may ignore the result and none of them act on it.
    /// </summary>
    bool TryWrite(ChatStreamEvent streamEvent);

    /// <summary>Drains the buffered events in order until <see cref="Complete" /> and the queue are both exhausted.</summary>
    IAsyncEnumerable<ChatStreamEvent> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The SSE consumer is gone (the browser disconnected or unsubscribed). Subsequent writes become no-ops. It
    ///     must NOT complete the queue: the pump treats a write fault as a persistence fault and would terminalize the
    ///     row <c>Failed</c>, and the run is deliberately still going — the persisted row and the resume registry are
    ///     the recovery surface, exactly as they are for a reload.
    /// </summary>
    void Detach();

    /// <summary>Signals end of stream. Buffered events still drain to a reader that is still attached.</summary>
    void Complete();
}
