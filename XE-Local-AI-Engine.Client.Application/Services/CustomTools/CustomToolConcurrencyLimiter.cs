namespace XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>
///     A process-wide singleton ceiling on how many custom-tool runs — host commands AND HTTP fetches — may be in flight at once, not a
///     per-request one.
/// </summary>
/// <remarks>
///     The host runner drops the agent sandbox's cgroup and netns wrapper, because a custom Command reaches the host by design, so this cap,
///     the per-run wall-clock timeout and the output/body byte cap are the affordable containment against a fan-out exhausting the box.
///     It is a concurrency-only ceiling: a stronger per-process resource ceiling (Linux <c>rlimit</c> AS/NPROC/CPU or a Windows Job Object)
///     would need a fork+setrlimit+exec or Job Object wrapper like the sandbox launcher's. Host network stays reachable from a Command tool —
///     stated honestly, not eliminated.
/// </remarks>
internal sealed class CustomToolConcurrencyLimiter : IDisposable
{
    /// <summary>Default simultaneous host-command ceiling. Small on purpose: custom tools are an operator convenience, not a workload.</summary>
    public const int DefaultMaxConcurrentRuns = 4;

    private readonly SemaphoreSlim _semaphore;

    public CustomToolConcurrencyLimiter(int maxConcurrentRuns = DefaultMaxConcurrentRuns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRuns);
        _semaphore = new SemaphoreSlim(maxConcurrentRuns, maxConcurrentRuns);
    }

    /// <summary>Acquires a run slot, releasing it when the returned handle is disposed. Honors cancellation while waiting.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Slot(_semaphore);
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private sealed class Slot : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Slot(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, value: null)?.Release();
        }
    }
}
