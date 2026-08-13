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
///     backlog. Admission never queues the same document concurrently. If the same id is admitted while already queued or
///     in flight, one deferred follow-up run is remembered; this is required when a repository update reuses the id while
///     the old revision is embedding. The worker calls <see cref="MarkCompleted" /> to schedule that deferred run. Accept /
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

    // A short lock keeps the admitted state and non-blocking channel write one atomic admission decision. Each document has
    // at most one queued/in-flight run plus one coalesced follow-up request, so updates never execute concurrently.
    private readonly Lock _admissionGate = new();
    private readonly Dictionary<Guid, AdmissionState> _admitted = [];

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

        lock (_admissionGate)
        {
            // Duplicate admission records one follow-up instead of queuing the same id concurrently. Repository replacement
            // reuses the id, so permanently collapsing that admission would leave the newly Pending revision unindexed.
            if (_admitted.TryGetValue(documentId, out var existing))
            {
                existing.RequeueRequested = true;
                return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.Accepted);
            }

            _admitted.Add(documentId, new AdmissionState());

            // Non-blocking admission: TryWrite succeeds while there is capacity and returns false the instant the queue is
            // full, so a burst is rejected rather than queued without bound or blocking the caller.
            if (_queue.Writer.TryWrite(documentId))
            {
                NodeMetrics.KnowledgeIngestionAcceptedTotal.Add(1);
                return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.Accepted);
            }

            // Full: undo the reservation so a later attempt (after capacity frees, e.g. the worker's drain-sweep) can admit it.
            _admitted.Remove(documentId);
            NodeMetrics.KnowledgeIngestionRejectedTotal.Add(1);
            return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.QueueFull);
        }
    }

    /// <summary>
    ///     Releases a document from the admitted set once the worker has finished processing it (reached a terminal state or
    ///     abandoned it at shutdown), so a later reindex or re-upload of the same id can be admitted again. Idempotent.
    /// </summary>
    public void MarkCompleted(Guid documentId)
    {
        lock (_admissionGate)
        {
            if (!_admitted.TryGetValue(documentId, out var state))
            {
                return;
            }

            if (state.RequeueRequested && _queue.Writer.TryWrite(documentId))
            {
                state.RequeueRequested = false;
                NodeMetrics.KnowledgeIngestionAcceptedTotal.Add(1);
                return;
            }

            // If the bounded queue filled before the deferred write, release admission. The current row remains Pending;
            // the worker's drain sweep re-admits it as capacity frees, and startup recovery is the crash-safe fallback.
            _admitted.Remove(documentId);
        }
    }

    private sealed class AdmissionState
    {
        public bool RequeueRequested { get; set; }
    }
}
