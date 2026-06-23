namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Default <see cref="IPendingFootprintLedger" />. A single <see cref="SemaphoreSlim" />(1,1) serializes every local
///     decide-commit, and an <see cref="Interlocked" />-maintained byte total tracks in-flight reservations. The gate is
///     held only for the short read-decide-reserve sequence (no inference runs under it), so it never blocks the actual
///     model run. Singleton.
/// </summary>
public sealed class PendingFootprintLedger : IPendingFootprintLedger, IDisposable
{
    private readonly SemaphoreSlim _decisionGate = new(initialCount: 1, maxCount: 1);
    private long _reservedBytes;

    /// <inheritdoc />
    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    /// <summary>Disposes the decide-commit gate. Invoked by the container on shutdown (the ledger is a singleton).</summary>
    public void Dispose()
    {
        _decisionGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<IDisposable> EnterDecisionAsync(CancellationToken ct)
    {
        await _decisionGate.WaitAsync(ct).ConfigureAwait(false);
        return new GateHandle(_decisionGate);
    }

    /// <inheritdoc />
    public IDisposable Reserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Interlocked.Add(ref _reservedBytes, bytes);
        return new Reservation(this, bytes);
    }

    private void Release(long bytes)
    {
        Interlocked.Add(ref _reservedBytes, -bytes);
    }

    // Releases the decide-commit gate exactly once. The capacity service disposes it as soon as the decision is
    // committed/abandoned — well before the spawned model runs.
    private sealed class GateHandle : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private int _released;

        public GateHandle(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _gate.Release();
            }
        }
    }

    // The ledger reservation handed to the caller on Allow; disposing it once subtracts the bytes back out. Idempotent
    // so a double-dispose (e.g. both a finally and a using) cannot drive the total negative.
    private sealed class Reservation : IDisposable
    {
        private readonly long _bytes;
        private readonly PendingFootprintLedger _ledger;
        private int _released;

        public Reservation(PendingFootprintLedger ledger, long bytes)
        {
            _ledger = ledger;
            _bytes = bytes;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ledger.Release(_bytes);
            }
        }
    }
}
