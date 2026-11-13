namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>The background idle-reap cadence every runtime supervisor runs until shutdown.</summary>
public static class IdleReapLoop
{
    /// <summary>
    ///     Calls <paramref name="reapOnce" /> every quarter of <paramref name="idleTimeToLive" /> (at least once a second)
    ///     until <paramref name="ct" /> is cancelled, which ends the loop normally.
    /// </summary>
    /// <remarks>A fraction of the TTL keeps eviction latency bounded without busy-spinning.</remarks>
    public static async Task RunAsync(TimeSpan idleTimeToLive, TimeProvider timeProvider, Func<Task> reapOnce, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(reapOnce);
        var interval = TimeSpan.FromTicks(Math.Max(idleTimeToLive.Ticks / 4, TimeSpan.FromSeconds(1).Ticks));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, timeProvider, ct).ConfigureAwait(false);
                await reapOnce().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
