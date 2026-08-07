namespace XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>
///     A global ceiling on how many custom-tool host commands may run at once. The host runner deliberately drops the
///     agent sandbox's cgroup/netns wrapper (a custom Command reaches the host by design), so this cap plus the
///     per-command wall-clock timeout and output byte-cap are the affordable containment against a fan-out of
///     concurrent host processes exhausting the box. Registered as a singleton so the limit is process-wide, not
///     per-request.
///     <para>
///         ponytail: this is the concurrency-only ceiling. A per-process resource ceiling (Linux <c>rlimit</c>
///         AS/NPROC/CPU, Windows Job Object) is the stronger control the plan names as the upgrade path; it needs a
///         fork+setrlimit+exec (or Job Object) wrapper like the sandbox launcher and is deferred. Host network stays
///         reachable from a Command tool — stated honestly, not eliminated.
///     </para>
/// </summary>
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
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
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
