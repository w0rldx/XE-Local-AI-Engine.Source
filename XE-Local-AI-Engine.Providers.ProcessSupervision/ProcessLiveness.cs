namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     The idle clock and reuse-path liveness bookkeeping of one resident runtime child, shared by every supervisor's
///     registry entry. Lock-free: atomic reads, exchanges and compare-and-swap only.
/// </summary>
public sealed class ProcessLiveness
{
    private int _consecutiveFailures;

    // Seeded to the spawn time, so a freshly ready child is not re-probed until a full interval has passed.
    private long _lastProbeTicks;
    private long _lastUsedTicks;

    /// <summary>Starts both clocks at <paramref name="startedUtc" />, the moment the child became ready.</summary>
    public ProcessLiveness(DateTimeOffset startedUtc)
    {
        _lastUsedTicks = startedUtc.UtcTicks;
        _lastProbeTicks = startedUtc.UtcTicks;
    }

    /// <summary>When the child was last handed out or touched; drives idle eviction.</summary>
    public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

    /// <summary>Refreshes the idle clock.</summary>
    public void MarkUsed(DateTimeOffset now) =>
        Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);

    /// <summary>
    ///     Atomically claims the right to run the reuse-path liveness probe: succeeds, advancing the probe clock to
    ///     <paramref name="now" />, only when at least <paramref name="interval" /> has elapsed since the last claim.
    /// </summary>
    /// <remarks>Serializes probes across concurrent reuses, so at most one HTTP probe runs per child per interval.</remarks>
    public bool TryClaimLivenessProbe(DateTimeOffset now, TimeSpan interval)
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastProbeTicks);
            if (now.UtcTicks - last < interval.Ticks)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastProbeTicks, now.UtcTicks, last) == last)
            {
                return true;
            }
        }
    }

    /// <summary>Resets the consecutive-failure count after a successful liveness probe.</summary>
    public void ResetLivenessFailures() =>
        Interlocked.Exchange(ref _consecutiveFailures, value: 0);

    /// <summary>Records a failed liveness probe and returns the new consecutive-failure count.</summary>
    public int RecordLivenessFailure() =>
        Interlocked.Increment(ref _consecutiveFailures);
}
