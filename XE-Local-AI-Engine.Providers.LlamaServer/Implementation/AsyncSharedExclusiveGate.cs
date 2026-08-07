namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     The smallest async shared/exclusive gate the supervisor needs: any number of callers may hold it SHARED at
///     once, an EXCLUSIVE holder runs alone. The BCL has no async reader/writer lock, and nothing here justifies a
///     general one — the exclusive side is a rare operator action (runtime install/remove, source build, exclusive
///     profiling) while the shared side is on every inference request's ensure path.
/// </summary>
/// <remarks>
///     <para>INVARIANTS</para>
///     <list type="number">
///         <item>
///             Exclusion is mutual and complete in both directions: no shared holder is admitted while an exclusive
///             holder runs, and <see cref="EnterExclusiveAsync" /> does not return until every shared holder admitted
///             before it has called <see cref="ExitShared" />.
///         </item>
///         <item>Shared holders never exclude each other, so one caller's slow work cannot head-of-line block another.</item>
///         <item>
///             Admission is FIFO (<see cref="SemaphoreSlim" />'s own ordering), so a pending exclusive acquire cannot
///             be starved by a continuous stream of shared acquires: it is served ahead of every shared acquire that
///             arrives after it.
///         </item>
///         <item>
///             A shared acquire holds the underlying semaphore only for an O(1) counter update, never for the caller's
///             work — that is the whole point of the type.
///         </item>
///     </list>
///     <para>
///         Neither side is re-entrant, and an exit must be paired with a successful enter (a cancelled or faulted
///         enter has already undone itself).
///     </para>
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
    ///     Admits the exclusive holder: takes the semaphore (so no further shared holder can be admitted) and then
    ///     waits for the shared holders already inside to drain. A cancelled wait releases the semaphore, so a
    ///     cancelled exclusive acquire never leaves the gate closed.
    /// </summary>
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
