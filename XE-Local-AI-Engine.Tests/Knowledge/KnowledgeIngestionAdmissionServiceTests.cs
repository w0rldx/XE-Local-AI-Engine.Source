namespace XE_Local_AI_Engine.Tests.Knowledge;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the shared admission rule extracted out of the knowledge upload endpoint and the repository
///     importer's per-file loop: a document the store WROTE (a fresh insert, or a repository file whose bytes changed)
///     is always queued; a dedupe hit is re-queued only from a RETRYABLE state (Pending — stranded by a full
///     queue — or Failed, which the app's own messages tell the user to retry) and left alone once Indexed or mid-flight;
///     the resolved status is reported even when nothing is enqueued; and a full bounded queue surfaces as QueueFull so
///     the endpoint answers with a retryable busy response.
/// </summary>
public sealed class KnowledgeIngestionAdmissionServiceTests
{
    [Test]
    public async Task AdmitStoredDocumentAsync_WhenFreshlyInserted_Enqueues()
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, KnowledgeDocumentStatus.Pending);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: true, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Contains(dispatcher.Enqueued, documentId);
        AssertEx.Equal(KnowledgeDocumentStatus.Pending, result.Status);
        AssertEx.False(result.QueueFull);
    }

    [Test]
    [Arguments(KnowledgeDocumentStatus.Pending)]
    [Arguments(KnowledgeDocumentStatus.Failed)]
    public async Task AdmitStoredDocumentAsync_WhenDedupeHitInRetryableState_ReEnqueues(KnowledgeDocumentStatus status)
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, status);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: false, CancellationToken.None).ConfigureAwait(false);

        // Content-hash dedupe never inserts a second row, so re-enqueueing is the only way a re-upload can retry.
        AssertEx.Contains(dispatcher.Enqueued, documentId);
        AssertEx.Equal(status, result.Status);
    }

    [Test]
    [Arguments(KnowledgeDocumentStatus.Indexed)]
    [Arguments(KnowledgeDocumentStatus.Embedding)]
    public async Task AdmitStoredDocumentAsync_WhenStoreUpdatedAnExistingDocument_Enqueues(KnowledgeDocumentStatus status)
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, status);

        // The repository importer's update path: the row was not inserted, but its bytes changed, so the already-Indexed
        // (or mid-flight) document must be reindexed rather than left on its stale content.
        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: true, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Contains(dispatcher.Enqueued, documentId);
        AssertEx.Equal(KnowledgeIngestionEnqueueResult.Accepted, result.Enqueue);
        AssertEx.False(result.QueueFull);
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

        var result = await service.AdmitStoredDocumentAsync(Guid.NewGuid(), wasWritten: false, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Empty(dispatcher.Enqueued);
        // The status still has to reach the response: the endpoint reports it on the deduplicated upload.
        AssertEx.Equal(status, result.Status);
        AssertEx.Null(result.Enqueue);
        AssertEx.False(result.QueueFull);
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenCatalogHasNoRow_TreatsTheDocumentAsPending()
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        var service = CreateService(dispatcher, status: null);

        var result = await service.AdmitStoredDocumentAsync(documentId, wasWritten: false, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(KnowledgeDocumentStatus.Pending, result.Status);
        AssertEx.Contains(dispatcher.Enqueued, documentId);
    }

    [Test]
    public async Task AdmitStoredDocumentAsync_WhenQueueIsFull_ReportsQueueFull()
    {
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.QueueFull);
        var service = CreateService(dispatcher, KnowledgeDocumentStatus.Pending);

        var result = await service.AdmitStoredDocumentAsync(Guid.NewGuid(), wasWritten: true, CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(result.QueueFull);
    }

    private static KnowledgeIngestionAdmissionService CreateService(IKnowledgeIngestionDispatcher dispatcher, KnowledgeDocumentStatus? status)
    {
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(status));
        return new KnowledgeIngestionAdmissionService(catalog, dispatcher);
    }

    // Hand-written fake for the ValueTask-returning dispatcher: records enqueued ids and returns a fixed admission result,
    // avoiding the ValueTask/analyzer friction of stubbing it through NSubstitute.
    private sealed class RecordingDispatcher(KnowledgeIngestionEnqueueResult result) : IKnowledgeIngestionDispatcher
    {
        public List<Guid> Enqueued { get; } = [];

        public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Enqueued.Add(documentId);
            return ValueTask.FromResult(result);
        }
    }
}
