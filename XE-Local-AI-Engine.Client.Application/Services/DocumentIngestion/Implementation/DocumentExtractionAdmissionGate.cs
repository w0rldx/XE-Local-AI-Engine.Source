namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion.Implementation;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

/// <summary>
///     Bounds how many synchronous, in-request document extractions run at once, over a <see cref="SemaphoreSlim"/>.
///     Singleton — the count is process-wide, shared across every concurrent upload request.
/// </summary>
/// <remarks>
///     Each extraction buffers a whole upload, up to the per-file cap, in memory and parses it, so unbounded concurrent
///     uploads can aggregate to an out-of-memory condition even when every single file is within its size cap. The
///     conversation upload endpoint acquires admission before extracting and rejects with a busy status when the gate is
///     full.
/// </remarks>
public sealed class DocumentExtractionAdmissionGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public DocumentExtractionAdmissionGate()
        : this(DocumentExtractionLimits.DefaultMaxConcurrentExtractions)
    {
    }

    // Count is overridable so tests can drive the gate to capacity without spinning up the default number of extractions.
    internal DocumentExtractionAdmissionGate(int maxConcurrentExtractions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentExtractions);
        _semaphore = new SemaphoreSlim(maxConcurrentExtractions, maxConcurrentExtractions);
    }

    /// <summary>
    ///     Tries to admit one extraction without waiting. Returns <see langword="true"/> with a lease that MUST be
    ///     disposed when the extraction finishes (releasing the slot), or <see langword="false"/> when the gate is at
    ///     capacity and the caller should reject the request.
    /// </summary>
    public bool TryAcquire([NotNullWhen(true)] out IDisposable? lease)
    {
        // Non-blocking: admit immediately if a slot is free, otherwise reject so the request fails fast with a busy
        // status instead of piling up waiting requests (each holding an upload buffer in memory).
        if (_semaphore.Wait(0, CancellationToken.None))
        {
            lease = new Lease(_semaphore);
            return true;
        }

        lease = null;
        return false;
    }

    public void Dispose()
    {
        _semaphore.Dispose();
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
