namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Collections.Concurrent;

/// <summary>
///     One mutual-exclusion gate per caller-managed session id, held from session resolution through the accept
///     transaction's return, and again around each close-or-delete's busy check PLUS its mutation.
///     <para>
///         The admission transaction is a hard node-wide and per-principal bound, but it counts nothing per SESSION.
///         Two accepts that both read "no execution is active on this session" would both persist a seed into the SAME
///         conversation, and the first execution would then read the second caller's input as history. That is
///         cross-request contamination on an externally reachable surface, which is why the busy read has to sit inside
///         the same critical section as the write it authorises — a lock taken after the decision guards nothing.
///     </para>
///     <para>
///         A <c>PerInvocation</c> accept and a NEW caller-managed session take no gate: there is no session id to name
///         until the row exists, and nothing else can name a session that does not yet exist.
///     </para>
/// </summary>
/// <remarks>
///     A singleton because the accept path and the session service are both scoped and must share it — the same shape
///     <see cref="IntegrationCancellationRegistry" /> already has for cancellation handles.
/// </remarks>
// ponytail: a ConcurrentDictionary of semaphores, not a lock manager. The node is single-process and admission is 8
// deep; if a session ever needs fairness or wait timeouts, that is when to grow this.
internal sealed class IntegrationSessionGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    /// <summary>Test seam: how many live sessions currently hold an entry, so <see cref="Forget" /> is provable.</summary>
    internal int TrackedCount => _gates.Count;

    /// <summary>Takes the session's gate, releasing it when the returned lease is disposed.</summary>
    public async Task<IDisposable> EnterAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>
    ///     Drops a closed or deleted session's entry, so the map tracks live caller-managed sessions only. Called from
    ///     INSIDE the critical section: a caller already waiting on the old semaphore still gets it, finds the session
    ///     gone and answers 404 on its own checks — which is why the semaphore is dropped rather than disposed.
    /// </summary>
    public void Forget(Guid sessionId) =>
        _ = _gates.TryRemove(sessionId, out _);

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            // Idempotent: releasing twice would hand the gate to two holders at once.
            var released = Interlocked.Exchange(ref _gate, value: null);
            _ = released?.Release();
        }
    }
}
