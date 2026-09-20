namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>How many cost collections may be IN FLIGHT at once, for one application.</summary>
/// <remarks>
///     <see cref="PublishingDevWorkflowStore" />'s deadline bounds the caller's WAIT, not the collection: without a
///     ceiling a blocked collector holds a thread-pool worker and a service scope for the life of the process, one
///     per settle. A settle finding every slot taken goes ahead unmeasured, the trade the deadline already makes. A
///     container SINGLETON, since the scoped store makes a per-instance pool bound nothing and a static one would
///     span every application. ponytail: a counter, not a semaphore, because admission never waits; next rung a keyed pool.
/// </remarks>
internal sealed class DevWorkflowNodeTelemetryCollectionPool
{
    private int _inFlight;

    public DevWorkflowNodeTelemetryCollectionPool(int slots = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        Slots = slots;
    }

    /// <summary>The ceiling, for the log line that says a settle went without a measurement.</summary>
    public int Slots { get; }

    /// <summary>
    ///     Takes a slot if one is free, and NEVER waits for one: a settle that queued here would have swapped one
    ///     unbounded stall for another, on the path the deadline exists to keep short.
    /// </summary>
    public bool TryEnter()
    {
        while (true)
        {
            var inFlight = Volatile.Read(ref _inFlight);
            if (inFlight >= Slots)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _inFlight, inFlight + 1, inFlight) == inFlight)
            {
                return true;
            }
        }
    }

    /// <summary>Gives a slot back — when the COLLECTOR terminates, never when its caller stopped waiting for it.</summary>
    public void Release() =>
        _ = Interlocked.Decrement(ref _inFlight);
}
