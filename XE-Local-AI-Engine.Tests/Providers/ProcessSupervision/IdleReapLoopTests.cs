namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The idle-reap cadence every runtime supervisor runs in the background.</summary>
[Category(TestCategories.Unit)]
public sealed class IdleReapLoopTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(8, 2)]
    [Arguments(1, 1)]
    public async Task RunAsync_WaitsAQuarterOfTheTtl_AtLeastOneSecond_ThenReaps(int ttlSeconds, int expectedIntervalSeconds)
    {
        var timeProvider = new TimerCapturingTimeProvider();
        var reaps = 0;
        using var cts = new CancellationTokenSource();

        var loop = IdleReapLoop.RunAsync(() => TimeSpan.FromSeconds(ttlSeconds), timeProvider, () =>
        {
            Interlocked.Increment(ref reaps);
            return Task.CompletedTask;
        }, NullLogger.Instance, cts.Token);

        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 1, Timeout, "The loop must arm its first delay.");
        AssertEx.Equal(TimeSpan.FromSeconds(expectedIntervalSeconds), timeProvider.LastDueTime);
        AssertEx.Equal(expected: 0, Volatile.Read(ref reaps), "Nothing is reaped before the first interval elapses.");

        timeProvider.Fire();

        await AssertEx.EventuallyAsync(() => Volatile.Read(ref reaps) == 1 && timeProvider.TimersCreated == 2, Timeout,
            "One elapsed interval reaps once and arms the next delay.");

        await cts.CancelAsync();
        await loop;
        AssertEx.Equal(expected: 1, Volatile.Read(ref reaps), "Cancellation ends the loop normally, without another reap.");
    }

    [Test]
    public async Task RunAsync_RereadsTheTtlBeforeEveryDelay()
    {
        var timeProvider = new TimerCapturingTimeProvider();
        var ttl = TimeSpan.FromSeconds(8);
        using var cts = new CancellationTokenSource();

        var loop = IdleReapLoop.RunAsync(() => ttl, timeProvider, () => Task.CompletedTask, NullLogger.Instance, cts.Token);

        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 1, Timeout, "The loop must arm its first delay.");
        AssertEx.Equal(TimeSpan.FromSeconds(2), timeProvider.LastDueTime);

        // A TTL changed on the running node paces the very next delay.
        ttl = TimeSpan.FromSeconds(40);
        timeProvider.Fire();

        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 2, Timeout, "One elapsed interval arms the next delay.");
        AssertEx.Equal(TimeSpan.FromSeconds(10), timeProvider.LastDueTime);

        await cts.CancelAsync();
        await loop;
    }

    [Test]
    [Arguments(3600)]
    [Arguments(86_400)]
    public async Task RunAsync_CapsTheDelayAtThirtySeconds(int ttlSeconds)
    {
        var timeProvider = new TimerCapturingTimeProvider();
        using var cts = new CancellationTokenSource();

        var loop = IdleReapLoop.RunAsync(() => TimeSpan.FromSeconds(ttlSeconds), timeProvider, () => Task.CompletedTask, NullLogger.Instance, cts.Token);

        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 1, Timeout, "The loop must arm its first delay.");
        AssertEx.Equal(TimeSpan.FromSeconds(30), timeProvider.LastDueTime, "A long TTL must not delay a pass, and so a changed TTL or cap, past thirty seconds.");

        await cts.CancelAsync();
        await loop;
    }

    [Test]
    public async Task RunAsync_APassThatThrows_IsLogged_AndTheNextPassStillRuns()
    {
        var timeProvider = new TimerCapturingTimeProvider();
        var logger = new RecordingLogger<IdleReapLoopTests>();
        var reaps = 0;
        using var cts = new CancellationTokenSource();

        var loop = IdleReapLoop.RunAsync(() => TimeSpan.FromSeconds(8), timeProvider, () =>
        {
            return Interlocked.Increment(ref reaps) == 1
                ? Task.FromException(new InvalidOperationException("teardown failed"))
                : Task.CompletedTask;
        }, logger, cts.Token);

        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 1, Timeout, "The loop must arm its first delay.");
        timeProvider.Fire();
        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 2, Timeout, "A failed pass must not end the loop.");
        timeProvider.Fire();
        await AssertEx.EventuallyAsync(() => Volatile.Read(ref reaps) == 2 && timeProvider.TimersCreated == 3, Timeout, "The pass after the failure must run.");

        AssertEx.True(logger.Entries.Any(static entry => entry.Level == LogLevel.Warning && entry.Exception is InvalidOperationException), "The failed pass must be logged.");
        AssertEx.False(loop.IsCompleted, "The loop runs until it is cancelled.");
        await cts.CancelAsync();
        await loop;
    }

    [Test]
    public async Task RunAsync_APassCancelledByAnotherToken_IsLogged_AndTheNextPassStillRuns()
    {
        var timeProvider = new TimerCapturingTimeProvider();
        var logger = new RecordingLogger<IdleReapLoopTests>();
        var reaps = 0;
        using var cts = new CancellationTokenSource();

        // Only the loop's own token is shutdown; a cancellation from inside the pass (a timed-out probe, say) is a failed pass.
        var loop = IdleReapLoop.RunAsync(() => TimeSpan.FromSeconds(8), timeProvider, () =>
        {
            return Interlocked.Increment(ref reaps) == 1
                ? Task.FromException(new OperationCanceledException("probe timed out"))
                : Task.CompletedTask;
        }, logger, cts.Token);

        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 1, Timeout, "The loop must arm its first delay.");
        timeProvider.Fire();
        await AssertEx.EventuallyAsync(() => timeProvider.TimersCreated == 2, Timeout, "A pass cancelled by another token must not end the loop.");
        timeProvider.Fire();
        await AssertEx.EventuallyAsync(() => Volatile.Read(ref reaps) == 2 && timeProvider.TimersCreated == 3, Timeout, "The pass after it must run.");

        AssertEx.True(logger.Entries.Any(static entry => entry.Level == LogLevel.Warning && entry.Exception is OperationCanceledException), "The cancelled pass must be logged.");
        await cts.CancelAsync();
        await loop;
    }

    [Test]
    public async Task RunAsync_WhenTheTimerCannotBeCreated_FaultsInsteadOfSpinning()
    {
        var logger = new RecordingLogger<IdleReapLoopTests>();
        var reaps = 0;
        using var cts = new CancellationTokenSource();

        // Only a pass is retried: a broken clock would otherwise turn the loop into a hot spin of warnings with no delay between them.
        var loop = IdleReapLoop.RunAsync(() => TimeSpan.FromSeconds(8), new ThrowingTimerTimeProvider(), () =>
        {
            Interlocked.Increment(ref reaps);
            return Task.CompletedTask;
        }, logger, cts.Token);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => loop.WaitAsync(Timeout));
        AssertEx.Equal(expected: 0, Volatile.Read(ref reaps));
        AssertEx.Empty(logger.Entries, "A delay failure is not a pass failure: nothing is retried or logged.");
    }

    private sealed class ThrowingTimerTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            throw new InvalidOperationException("no timer");
    }

    /// <summary>Captures each delay's timer so a test fires it deterministically; the timer never fires on its own.</summary>
    private sealed class TimerCapturingTimeProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        private int _timersCreated;
        private long _lastDueTicks;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public TimeSpan LastDueTime => TimeSpan.FromTicks(Interlocked.Read(ref _lastDueTicks));

        public void Fire() =>
            Volatile.Read(ref _callback)?.Invoke(Volatile.Read(ref _state));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Volatile.Write(ref _state, state);
            Volatile.Write(ref _callback, callback);
            Interlocked.Exchange(ref _lastDueTicks, dueTime.Ticks);
            Interlocked.Increment(ref _timersCreated);
            return new InertTimer();
        }

        private sealed class InertTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) =>
                true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }
}
