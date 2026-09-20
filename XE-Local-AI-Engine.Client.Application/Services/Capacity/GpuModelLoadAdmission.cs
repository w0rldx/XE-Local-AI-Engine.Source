namespace XE_Local_AI_Engine.Client.Services.Capacity;

using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Default <see cref="IGpuModelLoadAdmission" />: one process-wide <see cref="SemaphoreSlim" />(1,1), shared by the
///     llama-server and stable-diffusion.cpp supervisors, serializing every GPU-backed load's spawn-through-readiness window.
/// </summary>
/// <remarks>
///     Serialization is the whole mechanism — two <c>--fit</c> loads must never read the same free-VRAM snapshot and oversubscribe the device.
///     The next waiter's <c>--fit</c> re-reads free VRAM once the holder's load is resident, so no byte-level accounting is invented here.
///     A cancelled waiter abandons the wait cleanly, leaving the semaphore untouched; the holder always releases via the returned ticket's
///     <see cref="IDisposable.Dispose" />, and the bounded <see cref="GpuModelLoadAdmissionOptions.MaxWait" /> surfaces a counted
///     <see cref="GpuModelLoadAdmissionTimeoutException" /> rather than hanging. Waits, timeouts and the live holding/waiting counts report on the <c>XE.Node</c> meter.
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

    public GpuModelLoadAdmission(GpuModelLoadAdmissionOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _maxWait = options.MaxWait;
        _timeProvider = timeProvider;
        (_activeGauge, _waitingGauge) = NodeMetrics.CreateGpuModelLoadAdmissionGauges(() => Volatile.Read(ref _active),
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
            await _gate.WaitAsync(timeoutCts.Token);
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
    private sealed class Ticket : IDisposable
    {
        private readonly GpuModelLoadAdmission _owner;
        private int _disposed;

        public Ticket(GpuModelLoadAdmission owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                _owner.Release();
            }
        }
    }
}
