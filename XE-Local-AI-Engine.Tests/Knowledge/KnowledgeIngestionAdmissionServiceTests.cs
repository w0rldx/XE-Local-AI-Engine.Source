namespace XE_Local_AI_Engine.Tests.Knowledge;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The admission rule shared by the upload endpoint and the repository importer: a document the store WROTE is queued
///     unless already finished or gone, and a dedupe hit is re-queued only from a RETRYABLE state (Pending or Failed).
/// </summary>
/// <remarks>
///     Indexed or mid-flight documents are left alone, the resolved status is reported even when nothing is enqueued,
///     and a full bounded queue surfaces as QueueFull so the endpoint answers with a retryable busy response.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class KnowledgeIngestionAdmissionServiceTests
{
    [Test]
    public async Task AdmitStoredDocumentAsync_WhenFreshlyInserted_Enqueues()
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, KnowledgeDocumentStatus.Pending);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: true, CancellationToken.None);

        AssertEx.Contains(dispatcher.Enqueued, documentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Pending, result.Status);
        AssertEx.False(result.QueueFull);
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenTheWrittenPendingRowIsIndexedBeforePublication_PublishesNoPendingAfterIndexed()
    {
        var documentId = Guid.NewGuid();
        var pushes = new List<string>();
        var notifier = RecordingNotifier(pushes);
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        // Read Pending, then a drain sweep runs the freshly written row to Indexed before this admission publishes.
        catalog.GetStatusAsync(documentId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            _ = notifier.NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Indexed, call.Arg<CancellationToken>());
            return Task.FromResult<KnowledgeDocumentStatus?>(KnowledgeDocumentStatus.Pending);
        });
        var service = new KnowledgeIngestionAdmissionService(catalog, new KnowledgeIngestionDispatcher(notifier), notifier);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: true, CancellationToken.None);

        // The follow-up run skips the Indexed row without a push, so a Pending here would hold the feed's batch running.
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, result.Enqueue);
        AssertEx.Equal("Indexed | refresh", string.Join(" | ", pushes));
    }

    [Test]
    [Arguments(KnowledgeDocumentStatus.Pending)]
    [Arguments(KnowledgeDocumentStatus.Failed)]
    public async Task AdmitStoredDocumentAsync_WhenDedupeHitInRetryableState_ReEnqueues(KnowledgeDocumentStatus status)
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, status);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: false, CancellationToken.None);

        // Content-hash dedupe never inserts a second row, so re-enqueueing is the only way a re-upload can retry.
        AssertEx.Contains(dispatcher.Enqueued, documentId);
        AssertEx.Equal(status, result.Status);
    }

    [Test]
    [Arguments(KnowledgeDocumentStatus.Extracting)]
    [Arguments(KnowledgeDocumentStatus.Embedding)]
    public async Task AdmitStoredDocumentAsync_WhenTheWrittenRowIsInFlight_AdmitsAFollowUp(KnowledgeDocumentStatus status)
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, status);

        // The repository importer's update path while the replaced revision's run is still going: the admission must
        // coalesce one follow-up run for the new bytes.
        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: true, CancellationToken.None);

        AssertEx.Contains(dispatcher.Enqueued, documentId);
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, result.Enqueue);
        AssertEx.False(result.QueueFull);
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenTheWrittenRowIsPurgedBeforeTheRead_PublishesNoPendingAndAdmitsNothing()
    {
        var documentId = Guid.NewGuid();
        var pushes = new List<string>();
        var notifier = RecordingNotifier(pushes);
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(documentId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            _ = notifier.NotifyDocumentRemovedAsync(documentId, call.Arg<CancellationToken>());
            return Task.FromResult<KnowledgeDocumentStatus?>(null);
        });
        var dispatcher = new KnowledgeIngestionDispatcher(notifier);
        var service = new KnowledgeIngestionAdmissionService(catalog, dispatcher, notifier);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: true, CancellationToken.None);

        // The feed already dropped the member on its removal; a later Pending would reopen a batch nothing ever settles.
        AssertEx.Equal("removed | refresh", string.Join(" | ", pushes));
        AssertEx.Null(result.Enqueue);
        AssertEx.Equal(0, (int)dispatcher.PendingCount);
    }

    [Test]
    [Arguments(KnowledgeDocumentStatus.Indexed)]
    [Arguments(KnowledgeDocumentStatus.Failed)]
    public async Task AdmitStoredDocumentAsync_WhenASweepFinishesTheWrittenRowBeforeTheRead_PublishesNoPendingAfterIt(KnowledgeDocumentStatus terminal)
    {
        var documentId = Guid.NewGuid();
        var pushes = new List<string>();
        var notifier = RecordingNotifier(pushes);
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        // A drain sweep admitted the freshly written row and ran it to its end before this caller's admission read it.
        catalog.GetStatusAsync(documentId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            _ = notifier.NotifyDocumentChangedAsync(documentId, terminal, call.Arg<CancellationToken>());
            return Task.FromResult<KnowledgeDocumentStatus?>(terminal);
        });
        var dispatcher = new KnowledgeIngestionDispatcher(notifier);
        var service = new KnowledgeIngestionAdmissionService(catalog, dispatcher, notifier);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: true, CancellationToken.None);

        // A Pending after the terminal push would hold the feed's batch running; the run it promises would only repeat.
        AssertEx.Equal($"{terminal} | refresh", string.Join(" | ", pushes));
        AssertEx.Equal(terminal, result.Status);
        AssertEx.Null(result.Enqueue);
        AssertEx.Equal(0, (int)dispatcher.PendingCount);
    }

    [Test]
    [Arguments(KnowledgeDocumentStatus.Indexed)]
    [Arguments(KnowledgeDocumentStatus.Extracting)]
    [Arguments(KnowledgeDocumentStatus.Chunking)]
    [Arguments(KnowledgeDocumentStatus.Embedding)]
    public async Task AdmitStoredDocumentAsync_WhenDedupeHitIsDoneOrInFlight_DoesNotEnqueue(KnowledgeDocumentStatus status)
    {
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, status);

        var result = await service.AdmitStoredDocumentAsync(Guid.NewGuid(), wasWritten: false, CancellationToken.None);

        AssertEx.Empty(dispatcher.Enqueued);
        // The status still has to reach the response: the endpoint reports it on the deduplicated upload.
        AssertEx.Equal(status, result.Status);
        AssertEx.Null(result.Enqueue);
        AssertEx.False(result.QueueFull);
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenADedupeHitsRowIsGone_RefreshesWithoutAdmitting()
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        var service = CreateService(dispatcher, status: null, notifier);

        // Deleted after the store deduplicated onto it: there is nothing to ingest, and no status worth announcing.
        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: false, CancellationToken.None);

        AssertEx.Equal(KnowledgeDocumentStatus.Pending, result.Status);
        AssertEx.Null(result.Enqueue);
        AssertEx.Empty(dispatcher.Enqueued);
        await notifier.Received(1).NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>());
        await notifier.DidNotReceiveWithAnyArgs().NotifyDocumentChangedAsync(Guid.Empty, default, default);
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenTheReadPendingRowIsIndexedBeforePublication_PublishesNoPendingAfterIndexed()
    {
        var documentId = Guid.NewGuid();
        var pushes = new List<string>();
        var notifier = RecordingNotifier(pushes);
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        // The row is read Pending, then its in-flight run finishes and pushes Indexed before this admission publishes.
        catalog.GetStatusAsync(documentId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            _ = notifier.NotifyDocumentChangedAsync(documentId, KnowledgeDocumentStatus.Indexed, call.Arg<CancellationToken>());
            return Task.FromResult<KnowledgeDocumentStatus?>(KnowledgeDocumentStatus.Pending);
        });
        var service = new KnowledgeIngestionAdmissionService(catalog, new KnowledgeIngestionDispatcher(notifier), notifier);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: false, CancellationToken.None);

        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, result.Enqueue);
        AssertEx.Equal("Indexed | refresh", string.Join(" | ", pushes));
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenTheReadFailedRowIsDeletedBeforePublication_PublishesNoPendingForIt()
    {
        var documentId = Guid.NewGuid();
        var pushes = new List<string>();
        var notifier = RecordingNotifier(pushes);
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(documentId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            _ = notifier.NotifyDocumentRemovedAsync(documentId, call.Arg<CancellationToken>());
            return Task.FromResult<KnowledgeDocumentStatus?>(KnowledgeDocumentStatus.Failed);
        });
        var service = new KnowledgeIngestionAdmissionService(catalog, new KnowledgeIngestionDispatcher(notifier), notifier);

        _ = await service.AdmitStoredDocumentAsync(documentId, wasWritten: false, CancellationToken.None);

        // A Pending push after the removal would resurrect the deleted row as queued in every client's feed.
        AssertEx.Equal("removed | refresh", string.Join(" | ", pushes));
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenQueueIsFull_ReportsQueueFull()
    {
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.QueueFull);
        var service = CreateService(dispatcher, KnowledgeDocumentStatus.Pending);

        var result = await service.AdmitStoredDocumentAsync(Guid.NewGuid(), wasWritten: true, CancellationToken.None);

        AssertEx.True(result.QueueFull);
    }

    private static KnowledgeIngestionAdmissionService CreateService(IKnowledgeIngestionDispatcher dispatcher,
        KnowledgeDocumentStatus? status,
        IKnowledgeIndexingNotifier? notifier = null)
    {
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(status));
        return new KnowledgeIngestionAdmissionService(catalog, dispatcher, notifier ?? new NullKnowledgeIndexingNotifier());
    }

    // Every push in arrival order, so a test can prove no Pending lands after a newer truth.
    private static IKnowledgeIndexingNotifier RecordingNotifier(List<string> pushes)
    {
        var notifier = Substitute.For<IKnowledgeIndexingNotifier>();
        _ = notifier.NotifyDocumentChangedAsync(Arg.Any<Guid>(), Arg.Any<KnowledgeDocumentStatus>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        pushes.Add(call.ArgAt<KnowledgeDocumentStatus>(1).ToString());
                        return Task.CompletedTask;
                    });
        _ = notifier.NotifyDocumentRemovedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            pushes.Add("removed");
            return Task.CompletedTask;
        });
        _ = notifier.NotifyDocumentsChangedAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            pushes.Add("refresh");
            return Task.CompletedTask;
        });
        return notifier;
    }

    // Hand-written fake for the ValueTask-returning dispatcher: records enqueued ids and returns a fixed admission result,
    // avoiding the ValueTask/analyzer friction of stubbing it through NSubstitute.
    private sealed class RecordingDispatcher : IKnowledgeIngestionDispatcher
    {
        private readonly KnowledgeIngestionEnqueueResult _result;

        public RecordingDispatcher(KnowledgeIngestionEnqueueResult result)
        {
            _result = result;
        }

        public List<Guid> Enqueued { get; } = [];

        public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Enqueued.Add(documentId);
            return ValueTask.FromResult(_result);
        }
    }
}
