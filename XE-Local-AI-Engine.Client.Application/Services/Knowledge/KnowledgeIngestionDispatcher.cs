namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Collections.Concurrent;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     Default <see cref="IKnowledgeIngestionDispatcher" />. Owns the background ingestion queue as a BOUNDED
///     single-reader <see cref="Channel{T}" /> of document ids. Singleton: the queue outlives any request scope. The M2
///     downstream concurrency bound is still enforced by the worker's <c>SemaphoreSlim</c> (see
///     <see cref="KnowledgeIngestionWorker" />); the queue's own capacity bound is the admission control that keeps a
///     burst of uploads from accreting unbounded pending ids. Admission is non-blocking: a write that arrives while the
///     queue is full is rejected (<see cref="KnowledgeIngestionEnqueueResult.QueueFull" />) rather than dropped or awaited,
///     so the upload/reindex caller can return a retryable busy response instead of holding the request or growing the
///     backlog. Admission is also IDEMPOTENT: a document already queued or in flight is not enqueued again (a retry or a
///     drain-sweep of the same id is a no-op), so the same document is never processed twice concurrently. The worker calls
///     <see cref="MarkCompleted" /> once a document reaches a terminal state so a later reindex can re-admit it. Accept /
///     reject counts and the live queue depth are published on the <c>XE.Node</c> meter.
/// </summary>
public sealed class KnowledgeIngestionDispatcher : IKnowledgeIngestionDispatcher
{
    /// <summary>
    ///     Maximum number of documents that may be pending admission at once. The ids are tiny (a Guid each), so this
    ///     bounds pending admissions, not memory; it caps how far the upload endpoint can run ahead of the single-document
    ///     ingestion worker before uploads are told to retry, keeping a burst from deferring an unbounded amount of work.
    /// </summary>
    public const int Capacity = 256;

    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        // Admission uses TryWrite and never blocks, so this mode only governs a (never-taken) WriteAsync path; a full
        // queue surfaces as a QueueFull result at the call site rather than waiting or evicting.
        FullMode = BoundedChannelFullMode.Wait
    });

    // Document ids admitted but not yet completed (queued OR being processed). Makes admission idempotent: a retry or a
    // drain-sweep of a document already in this set is a no-op rather than a duplicate ingestion. Cleared by MarkCompleted.
    private readonly ConcurrentDictionary<Guid, byte> _admitted = new();

    public KnowledgeIngestionDispatcher()
    {
        // The gauge is owned (rooted) by the shared static XE.Node meter for the process lifetime; the singleton dispatcher
        // needs no reference back. Its callback reads this instance's live queue depth.
        _ = NodeMetrics.CreateKnowledgeIngestionQueueDepthGauge(() => PendingCount);
    }

    /// <summary>The queue reader the background worker drains.</summary>
    public ChannelReader<Guid> Reader => _queue.Reader;

    /// <summary>Current number of admitted-but-not-yet-drained document ids. Also published as the queue-depth gauge.</summary>
    public long PendingCount => _queue.Reader.Count;

    public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("A document id is required to enqueue ingestion.", nameof(documentId));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Idempotent: a document already queued or in flight is treated as accepted without a second enqueue, so a retry
        // (or a drain-sweep) of the same id can never queue it twice and cause a concurrent double ingestion.
        if (!_admitted.TryAdd(documentId, 0))
        {
            return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.Accepted);
        }

        // Non-blocking admission: TryWrite succeeds while there is capacity and returns false the instant the queue is
        // full, so a burst is rejected rather than queued without bound or blocking the caller.
        if (_queue.Writer.TryWrite(documentId))
        {
            NodeMetrics.KnowledgeIngestionAcceptedTotal.Add(1);
            return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.Accepted);
        }

        // Full: undo the reservation so a later attempt (after capacity frees, e.g. the worker's drain-sweep) can admit it.
        _admitted.TryRemove(documentId, out _);
        NodeMetrics.KnowledgeIngestionRejectedTotal.Add(1);
        return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.QueueFull);
    }

    /// <summary>
    ///     Releases a document from the admitted set once the worker has finished processing it (reached a terminal state or
    ///     abandoned it at shutdown), so a later reindex or re-upload of the same id can be admitted again. Idempotent.
    /// </summary>
    public void MarkCompleted(Guid documentId)
    {
        _admitted.TryRemove(documentId, out _);
    }
}
