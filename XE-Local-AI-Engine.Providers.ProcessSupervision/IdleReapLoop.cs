namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using Microsoft.Extensions.Logging;

/// <summary>The background idle-reap cadence every runtime supervisor runs until shutdown.</summary>
public static class IdleReapLoop
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    // The longest wait between two passes, so a TTL or cap changed on a running node applies within it.
    private static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Calls <paramref name="reapOnce" /> every quarter of the TTL <paramref name="idleTimeToLive" /> returns, re-read before
    ///     every delay and clamped to between one and thirty seconds, until <paramref name="ct" /> is cancelled, which ends the
    ///     loop normally.
    /// </summary>
    /// <remarks>
    ///     A fraction of the TTL keeps eviction latency bounded without busy-spinning. A pass that throws is logged and the next one
    ///     still runs: a faulted loop would never reap again until the node restarts.
    /// </remarks>
    public static async Task RunAsync(Func<TimeSpan> idleTimeToLive, TimeProvider timeProvider, Func<Task> reapOnce, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(idleTimeToLive);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(reapOnce);
        ArgumentNullException.ThrowIfNull(logger);
        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromTicks(Math.Clamp(idleTimeToLive().Ticks / 4, MinInterval.Ticks, MaxInterval.Ticks));
            try
            {
                await Task.Delay(interval, timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // Normal shutdown.
            }

            try
            {
                await reapOnce().ConfigureAwait(false);
            }
            catch (Exception exception) when (!ct.IsCancellationRequested)
            {
                // Any failure, an unrelated cancellation included, costs one pass: only the loop's own token ends it.
                logger.LogWarning(exception, "An idle-reap pass failed; the next pass runs on schedule.");
            }
        }
    }
}
