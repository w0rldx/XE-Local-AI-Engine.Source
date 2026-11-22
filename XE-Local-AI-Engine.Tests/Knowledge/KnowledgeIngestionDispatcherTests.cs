namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using NSubstitute;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Admission control for <see cref="KnowledgeIngestionDispatcher" />: the queue is BOUNDED, so an over-capacity
///     burst is rejected with a typed <see cref="KnowledgeIngestionEnqueueResult.QueueFull" /> (never dropped or
///     blocked), and the accept / reject counters and queue-depth gauge are published on the <c>XE.Node</c> meter.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class KnowledgeIngestionDispatcherTests
{
    [Test]
    public async Task EnqueueAsync_AdmitsUpToCapacityThenRejects()
    {
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());

        // Fill the queue exactly to capacity: every one of these must be admitted (nothing drains it — no worker runs).
        for (var i = 0; i < KnowledgeIngestionDispatcher.Capacity; i++)
        {
            var admitted = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
            AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, admitted);
        }

        AssertEx.Equal(KnowledgeIngestionDispatcher.Capacity, (int)dispatcher.PendingCount);

        // The queue is full: the next admission is rejected, not dropped and not blocked.
        var rejected = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.QueueFull, rejected);
        AssertEx.Equal(KnowledgeIngestionDispatcher.Capacity, (int)dispatcher.PendingCount);
    }

    [Test]
    public async Task EnqueueAsync_RefreshesWithoutAStatus_AndAStrandedReadmitOfAQueuedDocumentPushesNothing()
    {
        // The caller's Pending may already be stale when the push lands (a sweep indexed the row, a purge deleted it), so
        // clients only refetch; the run's own transitions open and settle the operations feed's batch.
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var dispatcher = new KnowledgeIngestionDispatcher(notifier);
        var documentId = Guid.NewGuid();

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, await dispatcher.EnqueueAsync(documentId, CancellationToken.None));
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, await dispatcher.EnqueueStrandedAsync(documentId, CancellationToken.None));

        await notifier.Received(1).NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>());
        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentChangedAsync(Guid.Empty, default, default);
    }

    [Test]
    public async Task EnqueueStrandedAsync_OfAnUnqueuedDocument_PushesAStatusFreeRefreshInsteadOfPending()
    {
        // A stranded row's status can move before the push lands (its run starts, or it is deleted), so the sweep only
        // tells clients to refetch; a Pending push could land after that run's Indexed and stick.
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var dispatcher = new KnowledgeIngestionDispatcher(notifier);
        var documentId = Guid.NewGuid();

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, await dispatcher.EnqueueStrandedAsync(documentId, CancellationToken.None));

        AssertEx.Equal(1, (int)dispatcher.PendingCount);
        await notifier.Received(1).NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>());
        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentChangedAsync(Guid.Empty, default, default);
    }

    [Test]
    public async Task EnqueueAsync_RefreshesAndCoalescesAnInFlightRetryIntoOneFollowUp()
    {
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var dispatcher = new KnowledgeIngestionDispatcher(notifier);
        var documentId = Guid.NewGuid();

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, await dispatcher.EnqueueAsync(documentId, CancellationToken.None));
        AssertEx.True(dispatcher.Reader.TryRead(out _));

        // A retry admitted while the first run is in flight keeps the normal coalescing: exactly one deferred run.
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, await dispatcher.EnqueueAsync(documentId, CancellationToken.None));
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, await dispatcher.EnqueueAsync(documentId, CancellationToken.None));
        AssertEx.False(dispatcher.Reader.TryRead(out _), "the follow-up stays deferred while the first run is active");
        dispatcher.MarkCompleted(documentId);
        AssertEx.True(dispatcher.Reader.TryRead(out var followUp));
        AssertEx.Equal(documentId, followUp);
        AssertEx.False(dispatcher.Reader.TryRead(out _), "the retries coalesce into one follow-up run");

        await notifier.Received(3).NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>());
        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentChangedAsync(Guid.Empty, default, default);
    }

    [Test]
    public async Task EnqueueAsync_WhenTheQueueIsFull_RejectsWithoutPublishingPending()
    {
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var dispatcher = new KnowledgeIngestionDispatcher(notifier);
        for (var i = 0; i < KnowledgeIngestionDispatcher.Capacity; i++)
        {
            _ = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
        }

        notifier.ClearReceivedCalls();
        var rejected = Guid.NewGuid();

        // A rejected retry leaves its row as it was (Failed, say): a Pending push would show it queued when it is not.
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.QueueFull, await dispatcher.EnqueueAsync(rejected, CancellationToken.None));

        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentChangedAsync(Guid.Empty, default, default);
        AssertEx.Equal(KnowledgeIngestionDispatcher.Capacity, (int)dispatcher.PendingCount);
    }

    [Test]
    public async Task EnqueueAsync_SameDocumentTwice_IsIdempotent_AndDoesNotDoubleQueue()
    {
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        var documentId = Guid.NewGuid();

        // A retry (or drain-sweep) of a document already queued must be a no-op accept, not a second queue entry, so the
        // document is never processed twice concurrently.
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(documentId, CancellationToken.None));
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(documentId, CancellationToken.None));

        AssertEx.Equal(1, (int)dispatcher.PendingCount);
    }

    [Test]
    public async Task EnqueueAsync_WhileDocumentIsInFlight_DefersExactlyOneFollowUpRun()
    {
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        var documentId = Guid.NewGuid();

        _ = await dispatcher.EnqueueAsync(documentId, CancellationToken.None);
        AssertEx.True(dispatcher.Reader.TryRead(out var firstRun));
        AssertEx.Equal(documentId, firstRun);

        // A repository replacement keeps the same document id. It must not run concurrently with the old revision, but
        // its admission must survive until completion so the replacement blob is eventually indexed.
        _ = await dispatcher.EnqueueAsync(documentId, CancellationToken.None);
        _ = await dispatcher.EnqueueAsync(documentId, CancellationToken.None);
        AssertEx.False(dispatcher.Reader.TryRead(out _), "The follow-up must remain deferred while the first run is active.");

        dispatcher.MarkCompleted(documentId);

        AssertEx.True(dispatcher.Reader.TryRead(out var followUp));
        AssertEx.Equal(documentId, followUp);
        AssertEx.False(dispatcher.Reader.TryRead(out _), "Multiple update admissions should coalesce into one follow-up run.");
    }

    [Test]
    public async Task EnqueueAsync_AfterMarkCompleted_ReAdmitsSameDocument()
    {
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        var documentId = Guid.NewGuid();

        _ = await dispatcher.EnqueueAsync(documentId, CancellationToken.None);
        // Worker drains it, then reports completion — the id leaves the admitted set so a later reindex can re-queue it.
        AssertEx.True(dispatcher.Reader.TryRead(out _));
        dispatcher.MarkCompleted(documentId);

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(documentId, CancellationToken.None));
        AssertEx.Equal(1, (int)dispatcher.PendingCount);
    }

    [Test]
    public async Task EnqueueAsync_WhenDrained_AdmitsAgain()
    {
        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());
        for (var i = 0; i < KnowledgeIngestionDispatcher.Capacity; i++)
        {
            _ = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
        }

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.QueueFull,
            await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None));

        // Drain one slot; the queue now has room and admits again — admission recovers, it is not a permanent latch.
        AssertEx.True(dispatcher.Reader.TryRead(out _));
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted,
            await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Test]
    public async Task EnqueueAsync_PublishesAcceptRejectCountersAndDepthGauge()
    {
        var accepted = 0L;
        var rejected = 0L;
        long? observedDepth = null;

        var dispatcher = new KnowledgeIngestionDispatcher(new NullKnowledgeIndexingNotifier());

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
            var result = await dispatcher.EnqueueAsync(Guid.NewGuid(), CancellationToken.None);
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

    [Test]
    public void DepthGauge_OnTheStaticMeter_DoesNotKeepTheNotifierAlive()
    {
        // The process-lifetime XE.Node meter roots the gauge. Were its callback to capture the dispatcher, the notifier
        // (and in a host, everything it reaches) would outlive the host: one leaked host per test host built in a run.
        var notifier = CreateDispatcherAndDropIt();

#pragma warning disable S1215 // Only a forced collection can show that nothing still roots the dropped notifier.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
#pragma warning restore S1215

        AssertEx.False(notifier.IsAlive, "the queue-depth gauge must not root the dispatcher's notifier.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDispatcherAndDropIt()
    {
        var notifier = new NullKnowledgeIndexingNotifier();
        _ = new KnowledgeIngestionDispatcher(notifier);
        return new WeakReference(notifier);
    }
}
