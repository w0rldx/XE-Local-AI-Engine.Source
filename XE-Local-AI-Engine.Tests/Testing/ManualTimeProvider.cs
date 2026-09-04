namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A <see cref="TimeProvider" /> whose clock only moves when a test moves it, and whose timers fire off that same
///     clock. The repo has several ad-hoc <c>FakeTimeProvider</c>/<c>AdjustableTimeProvider</c> copies that override
///     <see cref="GetUtcNow" /> only; those leave <see cref="CreateTimer" /> on the real system clock, so a
///     <see cref="PeriodicTimer" /> built over them still waits in wall-clock time. Services whose cadence is expressed
///     in minutes (the scheduler retention sweep) are untestable that way — hence this one, which also implements
///     <see cref="CreateTimer" />.
/// </summary>
/// <remarks>
///     Callbacks are invoked on the thread that calls <see cref="Advance" />, outside the internal lock, in due-time
///     order. That is enough to release a <see cref="PeriodicTimer.WaitForNextTickAsync" />; the continuation itself
///     still resumes on the thread pool, so a test asserts the resulting effect with
///     <see cref="AssertEx.EventuallyAsync" /> rather than assuming it ran by the time <c>Advance</c> returns.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>
    ///     How many timers are currently ARMED. A suite that advances before the code under test has registered its
    ///     timer moves the clock past a window nothing was waiting on, and then waits forever for an effect that will
    ///     never come.
    /// </summary>
    public int ArmedTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(static timer => timer.DueAtUtc is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>
    ///     The monotonic clock, moved by <see cref="Advance" /> like the wall clock. The base implementation reads
    ///     <see cref="System.Diagnostics.Stopwatch" />, which leaves any production code that measures elapsed time the
    ///     right way — <see cref="TimeProvider.GetElapsedTime(long)" /> — waiting in real seconds.
    /// </summary>
    public override long GetTimestamp() =>
        GetUtcNow().UtcTicks;

    /// <inheritdoc cref="GetTimestamp" />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that becomes due, repeating for periodic timers.</summary>
    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta), "Time only moves forward.");
        }

        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        // Step to each due instant in order so a periodic timer whose period is smaller than the advance fires once
        // per elapsed period rather than collapsing into a single tick.
        while (true)
        {
            ManualTimer? next = null;
            var nextDue = DateTimeOffset.MaxValue;
            lock (_gate)
            {
                foreach (var timer in _timers)
                {
                    if (timer.DueAtUtc is { } due && due <= target && due < nextDue)
                    {
                        nextDue = due;
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = nextDue;
            }

            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _ = _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAtUtc { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                DueAtUtc = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
            }

            return true;
        }

        public void Fire()
        {
            lock (owner._gate)
            {
                DueAtUtc = _period <= TimeSpan.Zero || _period == Timeout.InfiniteTimeSpan
                    ? null
                    : owner._now + _period;
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAtUtc = null;
            }

            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
