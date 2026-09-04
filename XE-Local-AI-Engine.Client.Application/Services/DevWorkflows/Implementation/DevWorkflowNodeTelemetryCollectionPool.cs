namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>
///     How many cost collections may be IN FLIGHT at once, for one application.
///     <para>
///         <see cref="PublishingDevWorkflowStore" />'s collection deadline bounds the caller's WAIT, not the collection
///         behind it. Without a ceiling on the collections themselves, a collector that blocks — or one that never
///         terminates at all — keeps a thread-pool worker and a service scope for as long as the process runs, one per
///         settle, and a wide retry route multiplies that by the graph's width. A settle that finds every slot taken
///         goes ahead unmeasured, which is exactly the trade the deadline already makes.
///     </para>
///     <para>
///         A container SINGLETON rather than a static field, and neither is an accident. The store around it is
///         registered scoped, so a per-instance pool would bound nothing; a static one would be shared by every
///         application a single process stands up — a ceiling meant for one runtime, applied to thirty of them at once,
///         which is a test suite losing measurements to its own neighbours rather than to a stuck collector.
///     </para>
/// </summary>
/// <remarks>
///     ponytail: a flat ceiling, no queue and no fairness — a refused settle is simply not measured. A counter rather
///     than a <c>SemaphoreSlim</c>, because admission here never WAITS, and a zero-timeout acquire buys nothing from
///     the semaphore's wait machinery except a handle to dispose. Raise the default if a box ever runs wide enough to
///     lose measurements to it; a per-run or per-graph budget is the next rung, and that one needs a keyed pool.
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
