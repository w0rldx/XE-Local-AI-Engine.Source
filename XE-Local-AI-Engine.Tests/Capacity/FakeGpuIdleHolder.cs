namespace XE_Local_AI_Engine.Tests.Capacity;

using XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     A hand-written <see cref="IGpuIdleHolder" /> with the session's state machine (admitting, active, idle, evicting): one
///     lock, the eviction started outside it and owned by the holder, demand latched while busy.
/// </summary>
/// <remarks>The real holder is the Model Lab session; this one gives back what <c>release</c> names, so the gates it holds are real.</remarks>
internal sealed class FakeGpuIdleHolder : IGpuIdleHolder
{
    private readonly Lock _sync = new();
    private readonly TimeProvider _clock;
    private readonly Func<Task> _release;
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HolderState _state = HolderState.Admitting;
    private bool _preemptionPending;
    private int _evictions;
    private int _requests;
    private Task _eviction = Task.CompletedTask;

    /// <param name="release">Gives back what the holder holds; its eviction runs it, then reports the release.</param>
    /// <param name="clock">Bounds <see cref="WaitForReleaseAsync" />, as the session's own clock does; the system clock when null.</param>
    public FakeGpuIdleHolder(Func<Task> release, TimeProvider? clock = null)
    {
        _release = release;
        _clock = clock ?? TimeProvider.System;
    }

    public enum HolderState
    {
        Admitting,
        Active,
        Idle,
        Evicting,
        Released
    }

    public HolderState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool PreemptionPending
    {
        get
        {
            lock (_sync)
            {
                return _preemptionPending;
            }
        }
    }

    /// <summary>How many evictions started; the holder starts at most one.</summary>
    public int EvictionCount => Volatile.Read(ref _evictions);

    /// <summary>How many times any demand reached this holder.</summary>
    public int PreemptionRequestCount => Volatile.Read(ref _requests);

    /// <summary>Completes once the eviction has given everything back.</summary>
    public Task Released => _released.Task;

    public Task Eviction
    {
        get
        {
            lock (_sync)
            {
                return _eviction;
            }
        }
    }

    public bool IsIdle => State == HolderState.Idle;

    public void RequestPreemption()
    {
        Interlocked.Increment(ref _requests);
        lock (_sync)
        {
            if (_state is HolderState.Admitting or HolderState.Active)
            {
                _preemptionPending = true;
                return;
            }

            if (_state != HolderState.Idle)
            {
                return;
            }

            _state = HolderState.Evicting;
            _eviction = StartEviction();
        }
    }

    /// <summary>A job starts on the holder: from admitting (cold) or from idle (warm).</summary>
    public void EnterActive()
    {
        lock (_sync)
        {
            if (_state is not (HolderState.Admitting or HolderState.Idle))
            {
                throw new InvalidOperationException($"A job cannot start while the holder is {_state}.");
            }

            _state = HolderState.Active;
        }
    }

    /// <summary>The job ended: idle, or straight to evicting when demand arrived meanwhile.</summary>
    public void ExitActive()
    {
        lock (_sync)
        {
            if (_state != HolderState.Active)
            {
                throw new InvalidOperationException($"No job is running while the holder is {_state}.");
            }

            if (!_preemptionPending)
            {
                _state = HolderState.Idle;
                return;
            }

            _state = HolderState.Evicting;
            _eviction = StartEviction();
        }
    }

    public async Task<bool> WaitForReleaseAsync(TimeSpan bound, CancellationToken ct)
    {
        if (State is HolderState.Admitting or HolderState.Active)
        {
            return false;
        }

        try
        {
            await _released.Task.WaitAsync(bound, _clock, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // Started under the lock but runs detached: no requester's token, and the release callback runs outside the lock.
    private Task StartEviction()
    {
        Interlocked.Increment(ref _evictions);
        return Task.Run(async () =>
        {
            await _release();
            lock (_sync)
            {
                _state = HolderState.Released;
            }

            _released.TrySetResult();
        }, CancellationToken.None);
    }
}
