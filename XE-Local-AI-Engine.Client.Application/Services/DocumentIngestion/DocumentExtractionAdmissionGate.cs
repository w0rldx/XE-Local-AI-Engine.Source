namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion.Extraction;

/// <summary>
///     <see cref="SemaphoreSlim"/>-backed admission gate for synchronous document extraction. Singleton — the count is
///     process-wide, shared across every concurrent upload request.
/// </summary>
public sealed class DocumentExtractionAdmissionGate : IDocumentExtractionAdmissionGate, IDisposable
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

    public bool TryAcquire([NotNullWhen(true)] out IDisposable? lease)
    {
        // Non-blocking: admit immediately if a slot is free, otherwise reject so the request fails fast with a busy
        // status instead of piling up waiting requests (each holding an upload buffer in memory).
        if (_semaphore.Wait(0))
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
