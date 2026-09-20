namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Collections.Concurrent;

/// <summary>
///     One mutual-exclusion gate per caller-managed session id, held from session resolution through the accept
///     transaction's return, and again around each close-or-delete's busy check PLUS its mutation.
/// </summary>
/// <remarks>
///     Admission bounds the node and the principal but counts nothing per SESSION, so two accepts both reading "no
///     execution is active here" would seed the SAME conversation and the first execution would read the second caller's
///     input as history — cross-request contamination on an externally reachable surface. The busy read therefore sits
///     inside the same critical section as the write it authorises. An accept naming no session takes no gate: nothing
///     can name a session that does not yet exist. A singleton, since accept and the session service must share it.
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
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    /// <summary>
    ///     Drops a closed or deleted session's entry, so the map tracks live caller-managed sessions only.
    /// </summary>
    /// <remarks>
    ///     Called from INSIDE the critical section: a caller already waiting on the old semaphore still gets it, finds
    ///     the session gone and answers 404 on its own checks — which is why the semaphore is dropped, not disposed.
    /// </remarks>
    public void Forget(Guid sessionId) =>
        _ = _gates.TryRemove(sessionId, out _);

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            // Idempotent: releasing twice would hand the gate to two holders at once.
            var released = Interlocked.Exchange(ref _gate, value: null);
            _ = released?.Release();
        }
    }
}
