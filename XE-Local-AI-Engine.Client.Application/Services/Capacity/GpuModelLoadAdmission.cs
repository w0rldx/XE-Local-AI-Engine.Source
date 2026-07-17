namespace XE_Local_AI_Engine.Client.Services.Capacity;

using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Default <see cref="IGpuModelLoadAdmission" /> (AUD4-06): a single process-wide <see cref="SemaphoreSlim" />(1,1)
///     that serializes the spawn-through-readiness window of every GPU-backed model load — llama-server AND
///     stable-diffusion.cpp — so two <c>--fit</c> loads never read the same free-VRAM snapshot concurrently and
///     oversubscribe the device. Serialization is the whole mechanism: when the current holder's load becomes resident
///     and releases, the next waiter's <c>--fit</c> reads fresh free VRAM (that IS the re-evaluation — no byte-level
///     accounting is invented here). Registered as a singleton; the two supervisors share this one instance.
/// </summary>
/// <remarks>
///     Cancellation-safe: a waiter whose token cancels abandons the wait cleanly (the semaphore is untouched), and the
///     holder always releases via the returned ticket's <see cref="IDisposable.Dispose" /> (the supervisor wraps it in a
///     <c>using</c>). The wait is bounded by <see cref="GpuModelLoadAdmissionOptions.MaxWait" />: on expiry a
///     <see cref="GpuModelLoadAdmissionTimeoutException" /> is surfaced (and counted) rather than hanging. Wait duration,
///     timeouts, and the live holding/waiting counts are reported on the shared <c>XE.Node</c> meter.
/// </remarks>
public sealed class GpuModelLoadAdmission : IGpuModelLoadAdmission, IDisposable
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly TimeSpan _maxWait;
    private readonly TimeProvider _timeProvider;

    // Live counts backing the observable gauges: _active is 0 or 1 (the gate is a serializer); _waiting is the queue.
    private readonly ObservableGauge<long> _activeGauge;
    private readonly ObservableGauge<long> _waitingGauge;
    private int _active;
    private int _waiting;

    public GpuModelLoadAdmission(GpuModelLoadAdmissionOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _maxWait = options.MaxWait;
        _timeProvider = timeProvider ?? TimeProvider.System;
        (_activeGauge, _waitingGauge) = NodeMetrics.CreateGpuModelLoadAdmissionGauges(
            () => Volatile.Read(ref _active),
            () => Volatile.Read(ref _waiting));
    }

    /// <inheritdoc />
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var startTimestamp = _timeProvider.GetTimestamp();
        Interlocked.Increment(ref _waiting);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_maxWait);
        try
        {
            await _gate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The bounded max-wait elapsed (NOT a caller cancellation) — surface a typed timeout rather than hang.
            NodeMetrics.GpuModelLoadAdmissionTimeoutTotal.Add(1);
            throw new GpuModelLoadAdmissionTimeoutException();
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        Interlocked.Increment(ref _active);
        NodeMetrics.GpuModelLoadAdmissionWaitMs.Record(_timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds);
        return new Ticket(this);
    }

    /// <summary>Disposes the serialization semaphore. Invoked by the container on shutdown (the gate is a singleton).</summary>
    public void Dispose()
    {
        // The observable gauges' callbacks only read plain int fields, so they remain safe after disposal; the static
        // XE.Node meter owns their lifetime. Only the semaphore needs releasing here.
        _ = _activeGauge;
        _ = _waitingGauge;
        _gate.Dispose();
    }

    private void Release()
    {
        Interlocked.Decrement(ref _active);
        _gate.Release();
    }

    // The admission ticket handed to a holder; disposing it once releases the gate for the next waiter. Idempotent so a
    // double-dispose (e.g. a using plus a defensive finally) cannot over-release the semaphore.
    private sealed class Ticket(GpuModelLoadAdmission owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
