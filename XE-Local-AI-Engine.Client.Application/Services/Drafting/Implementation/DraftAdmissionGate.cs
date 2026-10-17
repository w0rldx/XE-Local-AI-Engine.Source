namespace XE_Local_AI_Engine.Client.Services.Drafting.Implementation;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Non-queueing admission gate for AI-assisted drafting: one draft at a time, and none while an invocation holds
///     the node's invocation slot (a turn parked on a human does not), refused as a 409 rather than queued.
/// </summary>
/// <remarks>
///     A draft is a foreground generation on the same single local runtime an invocation uses, so it refuses rather
///     than waits, and the slot is process-wide. simplified: best-effort check-then-run — an invocation starting
///     between the check and the call still overlaps, as does background memory extraction, which bypasses the gate;
///     llama-server queues both, and the real fix is a cross-path admission service.
/// </remarks>
internal sealed class DraftAdmissionGate : IDisposable
{
    private readonly SemaphoreSlim _slot = new(initialCount: 1, maxCount: 1);
    private readonly IWorkerEventDispatcher _workerEventDispatcher;

    public DraftAdmissionGate(IWorkerEventDispatcher workerEventDispatcher)
    {
        _workerEventDispatcher = workerEventDispatcher ?? throw new ArgumentNullException(nameof(workerEventDispatcher));
    }

    /// <summary>
    ///     Admits at most one draft, and only while no invocation holds the node's invocation slot. Take the draft slot FIRST so two
    ///     simultaneous drafts cannot both observe an idle node, then hand it straight back when the invocation slot is held.
    /// </summary>
    public bool TryAcquire([NotNullWhen(true)] out IDisposable? lease)
    {
        lease = null;
#pragma warning disable MA0032 // zero-timeout poll is a TryEnter, not a blocking wait: a token would change nothing
        if (!_slot.Wait(0))
#pragma warning restore MA0032
        {
            return false;
        }

        if (_workerEventDispatcher.SlotHolder is not null)
        {
            _ = _slot.Release();
            return false;
        }

        lease = new Lease(_slot);
        return true;
    }

    public void Dispose()
    {
        _slot.Dispose();
    }

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _released;

        public Lease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            // Idempotent: release the slot exactly once even if the caller disposes twice.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ = _semaphore.Release();
            }
        }
    }
}
