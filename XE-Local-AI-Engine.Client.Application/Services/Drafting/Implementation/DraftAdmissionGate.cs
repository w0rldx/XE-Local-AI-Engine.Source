namespace XE_Local_AI_Engine.Client.Services.Drafting.Implementation;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Non-queueing admission gate for AI-assisted drafting. A draft is a foreground, operator-initiated generation on
///     the same single local runtime an invocation uses, so it must refuse rather than wait: one draft at a time
///     (<see cref="SemaphoreSlim" /> with a zero-timeout wait), and none at all while an invocation is in flight
///     (a non-terminal <see cref="IWorkerEventDispatcher.CurrentInvocation" /> or <see cref="IInvocationRunner.ActiveInvocationCount" />
///     — both are consulted because they terminalize at slightly different points). A refusal surfaces as a 409, never a
///     queued request. Singleton — the slot is process-wide.
/// </summary>
/// <remarks>
///     ponytail: best-effort check-then-run. An invocation that starts in the milliseconds between the busy check and the
///     model call still overlaps this draft, and background memory extraction bypasses the gate entirely (locked decision
///     7). Both overlaps are short and llama-server queues them; a real fix is a cross-path admission service, which is
///     its own epic.
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
    public bool TryAcquire([NotNullWhen(true)] out IDisposable? lease)
    {
        lease = null;
        if (!_slot.Wait(0))
        {
            return false;
        }

        // CurrentInvocation is the dispatcher's LAST invocation, not only a live one — it keeps the completed state
        // around for the status surface (live-verified: it still carries Status=Completed minutes after a chat turn
        // ends). Only a non-terminal status means the node is actually busy; treating any non-null state as busy
        // would leave drafting refusing 409 forever after the first chat turn of the process lifetime.
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
