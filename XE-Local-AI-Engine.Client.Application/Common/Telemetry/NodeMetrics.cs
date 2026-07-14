namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using System.Diagnostics.Metrics;

/// <summary>
///     Represents node metrics.
/// </summary>
public static class NodeMetrics
{
    public const string MeterName = "XE.Node";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    ///     Incremented each time the node detects a hash mismatch on a received runtime-package envelope.
    ///     Labels: reason (config_hash_mismatch | history_hash_mismatch).
    /// </summary>
    public static readonly Counter<long> EnvelopeHashMismatchTotal =
        Meter.CreateCounter<long>("envelope_hash_mismatch_total",
            description: "Number of envelope hash mismatches detected by the node.");

    /// <summary>
    ///     Incremented when a chat stream watchdog fires (no-first-chunk or inter-chunk stall).
    ///     Labels: reason (no_first_chunk_timeout | inter_chunk_stall_timeout).
    /// </summary>
    public static readonly Counter<long> ChatStreamWatchdogTimeoutTotal =
        Meter.CreateCounter<long>("chat_stream_watchdog_timeout_total",
            description: "Number of chat stream watchdog timeouts by reason.");

    /// <summary>
    ///     Incremented when a stalled provider stream is abandoned because its <c>MoveNextAsync</c> or
    ///     <c>DisposeAsync</c> ignored cooperative cancellation past the watchdog's bounded grace. Content-free (a count
    ///     only) — the deliberate cost of a wall-clock bound that a non-cooperative provider cannot defeat; the abandoned
    ///     enumerator's native resources may not be reclaimed until (if ever) it returns.
    /// </summary>
    public static readonly Counter<long> ChatStreamProviderAbandonedTotal =
        Meter.CreateCounter<long>("chat_stream_provider_abandoned_total",
            description: "Number of stalled provider streams abandoned after ignoring cancellation within the watchdog grace.");

    /// <summary>
    ///     Incremented when an invocation fails before producing output.
    ///     Labels: source (failure category, e.g. timeout | agent_runtime | provider_unreachable | unexpected).
    /// </summary>
    public static readonly Counter<long> InvocationFailedTotal =
        Meter.CreateCounter<long>("invocation_failed_total",
            description: "Number of failed invocations by failure source.");


    /// <summary>
    ///     Incremented (by the abandoned count) when the memory-extraction worker abandons in-flight extraction job(s) at
    ///     shutdown because they ignored cooperative cancellation past the drain deadline plus the fixed grace. Content-free
    ///     (a count only) — the deliberate cost of a bounded shutdown that never waits indefinitely.
    /// </summary>
    public static readonly Counter<long> MemoryExtractionAbandonedTotal =
        Meter.CreateCounter<long>("memory_extraction_abandoned_total",
            description: "Number of memory-extraction jobs abandoned at shutdown after ignoring cancellation within the grace.");

    /// <summary>
    ///     Incremented each time a document is admitted onto the bounded knowledge-ingestion queue (content-free — a count
    ///     only). Paired with <see cref="KnowledgeIngestionRejectedTotal" /> so accept/reject ratio and admission pressure
    ///     are observable without any document identity.
    /// </summary>
    public static readonly Counter<long> KnowledgeIngestionAcceptedTotal =
        Meter.CreateCounter<long>("knowledge_ingestion_accepted_total",
            description: "Number of documents admitted onto the bounded knowledge-ingestion queue.");

    /// <summary>
    ///     Incremented each time a document is rejected from the bounded knowledge-ingestion queue because it was at
    ///     capacity (the upload/reindex caller receives a retryable busy response). Content-free — a count only.
    /// </summary>
    public static readonly Counter<long> KnowledgeIngestionRejectedTotal =
        Meter.CreateCounter<long>("knowledge_ingestion_rejected_total",
            description: "Number of documents rejected from the bounded knowledge-ingestion queue because it was full.");

    /// <summary>
    ///     Per-stage wall-clock duration (milliseconds) of a knowledge-base retrieval, tagged by
    ///     <c>stage</c> (fts | embed | vector | hydrate | rerank | expand). Lets a slow retrieval be attributed to a
    ///     specific arm without logging any query or chunk text.
    /// </summary>
    public static readonly Histogram<double> KnowledgeSearchStageDurationMs =
        Meter.CreateHistogram<double>("knowledge_search_stage_duration_ms",
            unit: "ms",
            description: "Per-stage duration of a knowledge-base retrieval by stage (fts | embed | vector | hydrate | rerank | expand).");

    /// <summary>
    ///     Registers the observable gauge that reports the current depth of the bounded knowledge-ingestion queue on the
    ///     shared <c>XE.Node</c> meter. The queue owner (the singleton dispatcher) supplies the live count callback and
    ///     holds the returned instrument for its lifetime. Kept as a factory (rather than a static instrument) because the
    ///     depth is read from the live channel, which only the dispatcher instance can observe.
    /// </summary>
    public static ObservableGauge<long> CreateKnowledgeIngestionQueueDepthGauge(Func<long> observeDepth)
    {
        ArgumentNullException.ThrowIfNull(observeDepth);
        return Meter.CreateObservableGauge("knowledge_ingestion_queue_depth",
            observeDepth,
            unit: "documents",
            description: "Current number of documents pending in the bounded knowledge-ingestion queue.");
    }
}
