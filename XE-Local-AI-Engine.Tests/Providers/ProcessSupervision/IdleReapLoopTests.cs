namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

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

        var loop = IdleReapLoop.RunAsync(TimeSpan.FromSeconds(ttlSeconds), timeProvider, () =>
        {
            Interlocked.Increment(ref reaps);
            return Task.CompletedTask;
        }, cts.Token);

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
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
