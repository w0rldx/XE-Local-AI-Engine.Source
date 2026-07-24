namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Admission-control tests for <see cref="KnowledgeIngestionDispatcher" />: the queue is BOUNDED, so a
///     burst that exceeds capacity is rejected with a typed <see cref="KnowledgeIngestionEnqueueResult.QueueFull" />
///     result (never silently dropped or blocked), and the accept / reject counters plus the live queue-depth gauge are
///     published on the <c>XE.Node</c> meter.
/// </summary>
public sealed class KnowledgeIngestionDispatcherTests
{
    [Test]
    public async Task EnqueueAsync_AdmitsUpToCapacityThenRejects()
    {
        var dispatcher = new KnowledgeIngestionDispatcher();

        // Fill the queue exactly to capacity: every one of these must be admitted (nothing drains it — no worker runs).
        for (var i = 0; i < KnowledgeIngestionDispatcher.Capacity; i++)
        {
            var admitted = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
            AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, admitted);
        }

        AssertEx.Equal(KnowledgeIngestionDispatcher.Capacity, (int)dispatcher.PendingCount);

        // The queue is full: the next admission is rejected, not dropped and not blocked.
        var rejected = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.QueueFull, rejected);
        AssertEx.Equal(KnowledgeIngestionDispatcher.Capacity, (int)dispatcher.PendingCount);
    }

    [Test]
    public async Task EnqueueAsync_SameDocumentTwice_IsIdempotent_AndDoesNotDoubleQueue()
    {
        var dispatcher = new KnowledgeIngestionDispatcher();
        var documentId = Guid.NewGuid();

        // A retry (or drain-sweep) of a document already queued must be a no-op accept, not a second queue entry, so the
        // document is never processed twice concurrently.
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(documentId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(documentId, CancellationToken.None).ConfigureAwait(false));

        AssertEx.Equal(1, (int)dispatcher.PendingCount);
    }

    [Test]
    public async Task EnqueueAsync_AfterMarkCompleted_ReAdmitsSameDocument()
    {
        var dispatcher = new KnowledgeIngestionDispatcher();
        var documentId = Guid.NewGuid();

        _ = await dispatcher.EnqueueAsync(documentId, CancellationToken.None).ConfigureAwait(false);
        // Worker drains it, then reports completion — the id leaves the admitted set so a later reindex can re-queue it.
        AssertEx.True(dispatcher.Reader.TryRead(out _));
        dispatcher.MarkCompleted(documentId);

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(documentId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(1, (int)dispatcher.PendingCount);
    }

    [Test]
    public async Task EnqueueAsync_WhenDrained_AdmitsAgain()
    {
        var dispatcher = new KnowledgeIngestionDispatcher();
        for (var i = 0; i < KnowledgeIngestionDispatcher.Capacity; i++)
        {
            _ = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        }

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.QueueFull,
            await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));

        // Drain one slot; the queue now has room and admits again — admission recovers, it is not a permanent latch.
        AssertEx.True(dispatcher.Reader.TryRead(out _));
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));
    }

    [Test]
    public async Task EnqueueAsync_PublishesAcceptRejectCountersAndDepthGauge()
    {
        var accepted = 0L;
        var rejected = 0L;
        long? observedDepth = null;

        var dispatcher = new KnowledgeIngestionDispatcher();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NodeMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            switch (instrument.Name)
            {
                case "knowledge_ingestion_accepted_total":
                    Interlocked.Add(ref accepted, measurement);
                    break;
                case "knowledge_ingestion_rejected_total":
                    Interlocked.Add(ref rejected, measurement);
                    break;
                // The gauge fires for every live dispatcher's instrument; capture the reading that matches THIS
                // dispatcher's filled queue (the only one driven to full capacity in this test).
                case "knowledge_ingestion_queue_depth" when measurement == KnowledgeIngestionDispatcher.Capacity:
                    observedDepth = measurement;
                    break;
                default:
                    break;
            }
        });
        listener.Start();

        var acceptedResults = 0;
        var rejectedResults = 0;
        for (var i = 0; i < KnowledgeIngestionDispatcher.Capacity + 3; i++)
        {
            var result = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
            if (result == KnowledgeIngestionEnqueueResult.Accepted)
            {
                acceptedResults++;
            }
            else
            {
                rejectedResults++;
            }
        }

        listener.RecordObservableInstruments();

        AssertEx.Equal(KnowledgeIngestionDispatcher.Capacity, acceptedResults);
        AssertEx.Equal(3, rejectedResults);
        // Counters are process-shared, so assert lower bounds (other tests may also increment concurrently): this test
        // alone contributed Capacity accepts and 3 rejects.
        AssertEx.True(accepted >= KnowledgeIngestionDispatcher.Capacity, $"accepted counter should reflect at least {KnowledgeIngestionDispatcher.Capacity} admissions, saw {accepted}.");
        AssertEx.True(rejected >= 3, $"rejected counter should reflect at least 3 rejections, saw {rejected}.");
        AssertEx.True(observedDepth == KnowledgeIngestionDispatcher.Capacity,
            $"the queue-depth gauge should have reported the filled depth of {KnowledgeIngestionDispatcher.Capacity}, saw {observedDepth?.ToString() ?? "no matching reading"}.");
    }
}
