namespace XE_Local_AI_Engine.Client.Services.Drafting.Implementation;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Non-queueing admission gate for AI-assisted drafting: one draft at a time, and none at all while an
///     invocation is in flight, refused as a 409 rather than queued.
/// </summary>
/// <remarks>
///     A draft is a foreground generation on the same single local runtime an invocation uses, so it refuses rather
///     than waits, and the slot is process-wide. ponytail: best-effort check-then-run — an invocation starting
///     between the check and the call still overlaps, as does background memory extraction, which bypasses the gate;
///     llama-server queues both, and the real fix is a cross-path admission service.
/// </remarks>
internal sealed class DraftAdmissionGate : IDisposable
{
    private readonly IInvocationRunner _invocationRunner;
    private readonly SemaphoreSlim _slot = new(initialCount: 1, maxCount: 1);
    private readonly IWorkerEventDispatcher _workerEventDispatcher;

    public DraftAdmissionGate(IWorkerEventDispatcher workerEventDispatcher, IInvocationRunner invocationRunner)
    {
        _workerEventDispatcher = workerEventDispatcher ?? throw new ArgumentNullException(nameof(workerEventDispatcher));
        _invocationRunner = invocationRunner ?? throw new ArgumentNullException(nameof(invocationRunner));
    }

    /// <summary>
    ///     Admits at most one draft, and only when the node is otherwise idle. Take the slot FIRST so two simultaneous
    ///     drafts cannot both observe an idle node, then check the invocation signals and hand the slot straight back
    ///     when the node is busy.
    /// </summary>
    /// <remarks>
    ///     Both <see cref="IWorkerEventDispatcher.CurrentInvocation" /> and
    ///     <see cref="IInvocationRunner.ActiveInvocationCount" /> are consulted, because they terminalize at slightly
    ///     different points.
    /// </remarks>
    public bool TryAcquire([NotNullWhen(true)] out IDisposable? lease)
    {
        lease = null;
#pragma warning disable MA0032 // zero-timeout poll is a TryEnter, not a blocking wait: a token would change nothing
        if (!_slot.Wait(0))
#pragma warning restore MA0032
        {
            return false;
        }

        // CurrentInvocation is the dispatcher's LAST invocation, not only a live one, since it keeps the completed
        // state for the status surface. Only a non-terminal status means busy, or drafting 409s forever after turn one.
        if (_workerEventDispatcher.CurrentInvocation is { Status: InvocationStatus.Pending or InvocationStatus.Assigned or InvocationStatus.Running }
            || _invocationRunner.ActiveInvocationCount > 0)
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
