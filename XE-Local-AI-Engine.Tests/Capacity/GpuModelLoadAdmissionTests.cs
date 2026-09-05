namespace XE_Local_AI_Engine.Tests.Capacity;

using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The process-wide GPU-load admission gate: it serializes acquirers (a second waits for the first to
///     release), a cancelled waiter abandons the wait cleanly without stealing the gate, a bounded max-wait surfaces a
///     typed timeout rather than hanging, and it records the wait-duration + timeout metrics on the shared XE.Node meter.
/// </summary>
public sealed class GpuModelLoadAdmissionTests
{
    [Test]
    public async Task Acquire_SecondCaller_WaitsUntilFirstReleases()
    {
        using var gate = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions());
        var first = await gate.AcquireAsync(CancellationToken.None);

        var secondTask = gate.AcquireAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(secondTask, "the second acquire must not be admitted while the first ticket is held")
                      .ConfigureAwait(false);

        first.Dispose();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        second.Dispose();
    }

    [Test]
    public async Task Acquire_WaiterCancellation_ReleasesCleanly_AndDoesNotStealTheGate()
    {
        using var gate = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions());
        var first = await gate.AcquireAsync(CancellationToken.None);

        using var waiterCts = new CancellationTokenSource();
        var cancelledWaiter = gate.AcquireAsync(waiterCts.Token);
        await waiterCts.CancelAsync().ConfigureAwait(false);
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => cancelledWaiter).ConfigureAwait(false);

        // The gate is still held by `first`; a fresh acquire must still queue behind it, then proceed once released — the
        // cancelled waiter neither stole the gate nor corrupted the semaphore count.
        var third = gate.AcquireAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(third, "the cancelled waiter must not have handed the gate to the next acquire")
                      .ConfigureAwait(false);

        first.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Dispose();
    }

    [Test]
    public async Task Acquire_BoundedWaitElapses_ThrowsTypedTimeout_AndCountsIt()
    {
        using var gate = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions
        {
            MaxWait = TimeSpan.FromMilliseconds(50)
        });
        var first = await gate.AcquireAsync(CancellationToken.None);

        var timeouts = 0L;
        using var listener = StartMeterListener("gpu_admission_timeout_total", value => Interlocked.Add(ref timeouts, value));

        await AssertEx.ThrowsAsync<GpuModelLoadAdmissionTimeoutException>(() => gate.AcquireAsync(CancellationToken.None)).ConfigureAwait(false);

        // Process-shared counter — assert the lower bound this test contributed.
        AssertEx.True(Interlocked.Read(ref timeouts) >= 1, "gpu_admission_timeout_total should have been incremented");
        first.Dispose();
    }

    [Test]
    public async Task Acquire_QueuedThenAdmitted_RecordsWaitDuration()
    {
        using var gate = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions());
        var first = await gate.AcquireAsync(CancellationToken.None);

        var waitSamples = 0L;
        using var listener = StartHistogramListener("gpu_admission_wait_ms", () => Interlocked.Increment(ref waitSamples));

        var secondTask = gate.AcquireAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(secondTask, "the second acquire has to be queued before the first releases, or there is no wait to record")
                      .ConfigureAwait(false);
        first.Dispose();
        (await secondTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)).Dispose();

        AssertEx.True(Interlocked.Read(ref waitSamples) >= 1, "gpu_admission_wait_ms should have recorded the queued wait");
    }

    private static MeterListener StartMeterListener(string instrumentName, Action<long> onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == NodeMetrics.MeterName && instrument.Name == instrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => onMeasurement(measurement));
        listener.Start();
        return listener;
    }

    private static MeterListener StartHistogramListener(string instrumentName, Action onMeasurement)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == NodeMetrics.MeterName && instrument.Name == instrumentName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => onMeasurement());
        listener.Start();
        return listener;
    }
}
