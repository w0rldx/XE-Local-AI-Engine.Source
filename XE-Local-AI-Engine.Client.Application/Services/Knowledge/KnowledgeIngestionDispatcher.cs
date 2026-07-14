namespace XE_Local_AI_Engine.Client.Services.Knowledge;

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
///     backlog. Accept/reject counts and the live queue depth are published on the <c>XE.Node</c> meter.
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

        // Non-blocking admission: TryWrite succeeds while there is capacity and returns false the instant the queue is
        // full, so a burst is rejected rather than queued without bound or blocking the caller.
        if (_queue.Writer.TryWrite(documentId))
        {
            NodeMetrics.KnowledgeIngestionAcceptedTotal.Add(1);
            return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.Accepted);
        }

        NodeMetrics.KnowledgeIngestionRejectedTotal.Add(1);
        return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.QueueFull);
    }
}
