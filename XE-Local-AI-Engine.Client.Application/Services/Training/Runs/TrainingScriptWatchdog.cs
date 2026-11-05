namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>The bounds over one supervised Python script: its silence, its total duration and its exit. A script that breaks one has its process group killed.</summary>
/// <remarks>
///     A script wedged on a CUDA call prints nothing and cannot honor a cooperative stop, while it holds the GPU. The output loop and
///     a cancel write the instants and the watchdog loop reads them, so each is one word. The first failure reason recorded is the one kept.
/// </remarks>
internal sealed class TrainingScriptWatchdog
{
    private readonly TimeSpan _exitGrace;
    private readonly TimeSpan _inactivityTimeout;
    private readonly int _killedExitCode;
    private readonly TimeSpan _maxDuration;
    private readonly string _noExitReason;
    private readonly string _overrunReason;
    private readonly string _silentReason;
    private readonly DateTimeOffset _startedAt;
    private readonly TimeProvider _timeProvider;
    private string? _failureReason;
    private long _lastEventTicks;
    private long _stopRequestedTicks;

    /// <param name="inactivityTimeout">How long the script may go without a protocol event.</param>
    /// <param name="maxDuration">The ceiling on the whole run, measured from this constructor.</param>
    /// <param name="exitGrace">How long the script gets to exit after closing its output, or after a stop marked here.</param>
    /// <param name="timeProvider">The clock every bound is measured on.</param>
    /// <param name="silentReason">The failure reason when <paramref name="inactivityTimeout" /> passes.</param>
    /// <param name="overrunReason">The failure reason when <paramref name="maxDuration" /> passes.</param>
    /// <param name="noExitReason">The failure reason when a script that closed its output outlives <paramref name="exitGrace" />.</param>
    /// <param name="killedExitCode">What <see cref="WaitForExitOrEscalateAsync" /> answers for a script it had to kill.</param>
    public TrainingScriptWatchdog(TimeSpan inactivityTimeout,
        TimeSpan maxDuration,
        TimeSpan exitGrace,
        TimeProvider timeProvider,
        string silentReason,
        string overrunReason,
        string noExitReason,
        int killedExitCode)
    {
        _exitGrace = exitGrace;
        _inactivityTimeout = inactivityTimeout;
        _killedExitCode = killedExitCode;
        _maxDuration = maxDuration;
        _noExitReason = noExitReason;
        _overrunReason = overrunReason;
        _silentReason = silentReason;
        _timeProvider = timeProvider;
        _startedAt = timeProvider.GetUtcNow();
        _lastEventTicks = _startedAt.UtcTicks;
    }

    public string? FailureReason => Volatile.Read(ref _failureReason);

    public void Fail(string reason) =>
        _ = Interlocked.CompareExchange(ref _failureReason, reason, comparand: null);

    /// <summary>A protocol event arrived. Other output does not count: a library that spams warnings while the script is wedged would hold the bound off forever.</summary>
    public void MarkEvent() =>
        Volatile.Write(ref _lastEventTicks, _timeProvider.GetUtcNow().UtcTicks);

    /// <summary>
    ///     From here the only bound left is the exit grace, and it ends in a kill that records no failure, so the outcome is still the
    ///     Cancelled the operator asked for. A caller that never marks a stop keeps it cooperative under the other two bounds.
    /// </summary>
    public void MarkStopRequested() =>
        Volatile.Write(ref _stopRequestedTicks, _timeProvider.GetUtcNow().UtcTicks);

    /// <summary>Takes the kill callback rather than the handle: the watchdog's only power over the child is to end it.</summary>
    /// <param name="killGroup">Kills the script's process group.</param>
    /// <param name="ct">Cancelled when the stream has ended, which is a signal because the loop sleeps a whole interval between looks.</param>
    public async Task RunAsync(Action killGroup, CancellationToken ct)
    {
        var interval = PollInterval();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Read again here: the delay can complete in the same instant the stream closes, and judging silence on the way out
            // would record a finished run as failed for its last stillness.
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow().UtcTicks;
            if (Volatile.Read(ref _stopRequestedTicks) is var stopAt and not 0)
            {
                if (now - stopAt > _exitGrace.Ticks)
                {
                    killGroup();
                    return;
                }

                continue;
            }

            if (now - Volatile.Read(ref _lastEventTicks) > _inactivityTimeout.Ticks)
            {
                Fail(_silentReason);
            }
            else if (now - _startedAt.UtcTicks > _maxDuration.Ticks)
            {
                Fail(_overrunReason);
            }

            if (FailureReason is not null)
            {
                // Escalating to SIGKILL is the point: a cooperative stop is exactly what a wedged script cannot do.
                killGroup();
                return;
            }
        }
    }

    /// <summary>Waits for the child to reap itself once its output has closed, bounded.</summary>
    /// <remarks>
    ///     A script wedged after its last write would otherwise hold this wait, and with it the caller's capacity reservation and GPU
    ///     hold, forever. On expiry the group is killed and the reason recorded, and the status is not waited for a second time.
    /// </remarks>
    public async Task<int> WaitForExitOrEscalateAsync(ITrainingProcessHandle handle)
    {
        using var grace = new CancellationTokenSource(_exitGrace, _timeProvider);
        try
        {
            return await handle.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            handle.KillGroup();
            Fail(_noExitReason);
            return _killedExitCode;
        }
    }

    /// <summary>At most a second, and scaled down with the bounds, so a short one is not rounded up to the poll interval.</summary>
    private TimeSpan PollInterval()
    {
        var shortest = _exitGrace < _inactivityTimeout ? _exitGrace : _inactivityTimeout;
        return shortest < TimeSpan.FromSeconds(4) ? shortest / 4 : TimeSpan.FromSeconds(1);
    }
}
