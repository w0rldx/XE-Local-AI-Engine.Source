namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A clock that moves forward by each new timer's due time and fires it on the thread pool, so a poll never sleeps.
///     A timer fires once: its period and <c>Change</c> are ignored, and an infinite due time never fires.
/// </summary>
internal sealed class ImmediateTimerTimeProvider : TimeProvider
{
    private long _nowTicks;

    public ImmediateTimerTimeProvider(DateTimeOffset start)
    {
        _nowTicks = start.UtcTicks;
    }

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Read(ref _nowTicks), TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            return new InertTimer();
        }

        Interlocked.Add(ref _nowTicks, dueTime.Ticks);
        _ = Task.Run(() => callback(state));
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
