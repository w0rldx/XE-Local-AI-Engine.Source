namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     The smallest async shared/exclusive gate the supervisor needs: any number of callers may hold it SHARED at
///     once, and an EXCLUSIVE holder runs alone.
/// </summary>
/// <remarks>
///     The BCL has no async reader/writer lock and nothing here justifies a general one — the exclusive side is a rare
///     operator action (runtime install or remove, source build, exclusive profiling) while the shared side is on every
///     inference request's ensure path. Neither side is re-entrant, and an exit must be paired with a successful enter,
///     a cancelled or faulted enter having already undone itself. Its four invariants:
///     docs/wiki/03-local-runtime-and-providers.md, "The runtime-mutation gate".
/// </remarks>
internal sealed class AsyncSharedExclusiveGate : IDisposable
{
    private readonly SemaphoreSlim _exclusive = new(initialCount: 1, maxCount: 1);
    private readonly Lock _sync = new();
    private int _sharedCount;
    private TaskCompletionSource? _sharedDrained;

    /// <summary>
    ///     Admits a shared holder, waiting while an exclusive holder owns the gate. Passing THROUGH the exclusive
    ///     semaphore rather than reading a flag is what makes exclusion mutual and starvation-free.
    /// </summary>
    public async Task EnterSharedAsync(CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_sharedCount++ == 0)
                {
                    _sharedDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }
        finally
        {
            _exclusive.Release();
        }
    }

    /// <summary>Releases a shared holder admitted by <see cref="EnterSharedAsync" />.</summary>
    public void ExitShared()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (--_sharedCount == 0)
            {
                drained = _sharedDrained;
                _sharedDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    /// <summary>
    ///     Admits the exclusive holder: it takes the semaphore, so no further shared holder can be admitted, then
    ///     waits for the shared holders already inside to drain.
    /// </summary>
    /// <remarks>
    ///     A cancelled wait releases the semaphore, so a cancelled exclusive acquire never leaves the gate closed.
    /// </remarks>
    public async Task EnterExclusiveAsync(CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForSharedDrainedAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _exclusive.Release();
            throw;
        }
    }

    /// <summary>Releases the exclusive holder admitted by <see cref="EnterExclusiveAsync" />.</summary>
    public void ExitExclusive()
    {
        _exclusive.Release();
    }

    public void Dispose()
    {
        _exclusive.Dispose();
    }

    private Task WaitForSharedDrainedAsync()
    {
        lock (_sync)
        {
            return _sharedCount == 0 ? Task.CompletedTask : _sharedDrained!.Task;
        }
    }
}
