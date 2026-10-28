namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Races a child's readiness probe against the child exiting, so a runtime that dies while loading is reported the
///     moment it exits instead of after the whole readiness budget.
/// </summary>
public static class ProcessReadinessRace
{
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Runs <paramref name="waitForReady" /> against an exit watcher on one linked token, cancels the losing side the
    ///     instant the other wins and awaits it. A cancelled <paramref name="ct" /> propagates as
    ///     <see cref="OperationCanceledException" />.
    /// </summary>
    public static async Task<ProcessReadinessOutcome> RunAsync(IProcessTreeHandle handle,
        Func<CancellationToken, Task<bool>> waitForReady,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(waitForReady);
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readyTask = waitForReady(linkedCts.Token);
        var exitTask = WaitForExitAsync(handle, timeProvider, linkedCts.Token);

        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);

        if (winner == exitTask && handle.HasExited)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            return ProcessReadinessOutcome.Exited;
        }

        await linkedCts.CancelAsync().ConfigureAwait(false);
        await SwallowCancellationAsync(exitTask).ConfigureAwait(false);

        return await readyTask.ConfigureAwait(false) ? ProcessReadinessOutcome.Ready : ProcessReadinessOutcome.TimedOut;
    }

    /// <summary>Polls the exit flag until the child exits or <paramref name="ct" /> is cancelled.</summary>
    public static async Task WaitForExitAsync(IProcessTreeHandle handle, TimeProvider timeProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(timeProvider);
        while (!ct.IsCancellationRequested)
        {
            if (handle.HasExited)
            {
                return;
            }

            await Task.Delay(ExitPollInterval, timeProvider, ct).ConfigureAwait(false);
        }
    }

    private static async Task SwallowCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: this side of the race was cancelled because the other side won.
        }
    }
}
