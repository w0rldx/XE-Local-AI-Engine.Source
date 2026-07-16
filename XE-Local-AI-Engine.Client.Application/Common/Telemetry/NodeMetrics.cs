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
    ///     Incremented each time a native hardware probe (e.g. <c>nvidia-smi</c>) exceeds its wall-clock deadline and is
    ///     killed (process tree). A non-zero rate flags a wedged GPU driver / stalled tool that would otherwise hang
    ///     first-run provisioning or a capacity decision; the profiler degrades to the last cached profile or the CPU
    ///     default. Labels: probe (the probe tool name).
    /// </summary>
    public static readonly Counter<long> HardwareProbeTimeoutTotal =
        Meter.CreateCounter<long>("hardware_probe_timeout_total",
            description: "Number of native hardware probes killed after exceeding their wall-clock deadline, by probe.");

    /// <summary>
    ///     Incremented when a node SQLite operation surfaces write contention that outlived <c>busy_timeout</c>
    ///     (SQLITE_BUSY / SQLITE_LOCKED). A non-zero value signals real contention on the shared database file — under
    ///     WAL + busy_timeout this should be rare. Labels: code (busy | locked), path (ef | raw). Bounded cardinality —
    ///     never carries SQL text.
    /// </summary>
    public static readonly Counter<long> SqliteBusyTotal =
        Meter.CreateCounter<long>("sqlite_busy_total",
            description: "Number of node SQLite operations that failed with SQLITE_BUSY/SQLITE_LOCKED after the busy timeout, by code and path.");

    /// <summary>
    ///     Wall-clock duration (milliseconds) of the model-readiness (warm) phase that runs BEFORE the stream-idle
    ///     watchdog is armed — the separated cold-load window. Tagged by <c>outcome</c> (ready | failed). A warm reuse is
    ///     a small value; a genuine cold load is a large one, so the histogram shows how long readiness actually takes
    ///     and whether it is being paid on the warm path by mistake. Content-free (a duration only).
    /// </summary>
    public static readonly Histogram<double> ModelReadinessDurationMs =
        Meter.CreateHistogram<double>("model_readiness_duration_ms",
            unit: "ms",
            description: "Duration of the pre-stream model-readiness (warm) phase, tagged by outcome (ready | failed).");

    /// <summary>
    ///     Incremented once per model-readiness (warm) phase. Labels: outcome (ready | failed). Paired with
    ///     <see cref="ModelReadinessDurationMs" /> so a readiness-failure rate is observable without any model identity.
    /// </summary>
    public static readonly Counter<long> ModelReadinessTotal =
        Meter.CreateCounter<long>("model_readiness_total",
            description: "Number of pre-stream model-readiness (warm) phases by outcome (ready | failed).");

    /// <summary>
    ///     Incremented on each operator eject request and its result. Labels: outcome (requested | ejected |
    ///     timed_out_still_busy | forced | not_running). The <c>requested</c> increment fires once per call; the result
    ///     increment fires once with the terminal outcome, so accept/drain/timeout/force ratios are observable without
    ///     any model identity.
    /// </summary>
    public static readonly Counter<long> ModelEjectTotal =
        Meter.CreateCounter<long>("model_eject_total",
            description: "Number of operator eject requests and outcomes (requested | ejected | timed_out_still_busy | forced | not_running).");

    /// <summary>
    ///     Incremented when the runtime device audit (AUD4-03) detects a silent CPU fallback: the host advertises a GPU
    ///     but the SELECTED llama.cpp runtime cannot use it — a CPU variant was chosen on a GPU box, or a GPU variant
    ///     enumerated zero devices (e.g. the shipped Vulkan build under WSL2 with no Vulkan ICD). Fires once per detection
    ///     (the audit is cached per binary), so a non-zero value flags inference silently running on the CPU while the
    ///     UI/advisor sized models to VRAM. Labels: reason (cpu_variant | zero_devices).
    /// </summary>
    public static readonly Counter<long> DeviceFallbackTotal =
        Meter.CreateCounter<long>("device_fallback_total",
            description: "Number of detected silent CPU fallbacks (GPU expected but the selected runtime runs on the CPU), by reason.");

    /// <summary>
    ///     Wall-clock duration (milliseconds) a GPU-backed model load waited to acquire the process-wide GPU-load
    ///     admission gate (AUD4-06) before its spawn began. Near-zero under no contention; a large value means a load
    ///     queued behind another load's spawn-through-readiness window. Content-free (a duration only).
    /// </summary>
    public static readonly Histogram<double> GpuModelLoadAdmissionWaitMs =
        Meter.CreateHistogram<double>("gpu_admission_wait_ms",
            unit: "ms",
            description: "Time a GPU-backed model load waited for the process-wide GPU-load admission gate before spawning.");

    /// <summary>
    ///     Incremented when a GPU-load admission wait exceeded its bounded max-wait and surfaced a typed timeout rather
    ///     than hanging a chat turn (AUD4-06). A non-zero value flags a wedged load holding the gate past the backstop.
    ///     Content-free — a count only.
    /// </summary>
    public static readonly Counter<long> GpuModelLoadAdmissionTimeoutTotal =
        Meter.CreateCounter<long>("gpu_admission_timeout_total",
            description: "Number of GPU-load admission waits that exceeded the bounded max-wait and surfaced a typed timeout.");


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
    ///     Number of candidate chunk-vector rows scanned by one managed cosine vector search (content-free — a row count
    ///     only). The search reads every stored vector for the resolved embedding model, so this is the brute-force fan-out
    ///     the bounded top-k selection runs over; a rising distribution flags a corpus large enough to warrant an ANN index.
    /// </summary>
    public static readonly Histogram<long> KnowledgeVectorSearchCandidatesScanned =
        Meter.CreateHistogram<long>("knowledge_vector_search_candidates_scanned",
            unit: "rows",
            description: "Number of stored chunk-vector rows scanned by one managed cosine vector search.");

    /// <summary>
    ///     Wall-clock duration (milliseconds) of one managed cosine vector search — the pure scan/score/select cost, measured
    ///     inside the search itself (distinct from the service-observed <c>stage=vector</c> timing, which also spans the
    ///     factory resolution and awaits around it).
    /// </summary>
    public static readonly Histogram<double> KnowledgeVectorSearchDurationMs =
        Meter.CreateHistogram<double>("knowledge_vector_search_duration_ms",
            unit: "ms",
            description: "Duration of one managed cosine vector search (scan + score + bounded top-k selection).");

    /// <summary>
    ///     Knowledge-search query-embedding cache lookups, tagged by <c>result</c> (hit | miss). A high hit ratio means the
    ///     dominant embedding round trip is being skipped for repeated queries. Content-free — a count only.
    /// </summary>
    public static readonly Counter<long> KnowledgeQueryEmbeddingCacheLookupsTotal =
        Meter.CreateCounter<long>("knowledge_query_embedding_cache_lookups_total",
            description: "Knowledge-search query-embedding cache lookups by result (hit | miss).");

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

    /// <summary>
    ///     Registers the observable gauges that report how many GPU-backed model loads are currently HOLDING the
    ///     process-wide admission gate (0 or 1 — the gate is a serializer) and how many are WAITING behind it (AUD4-06).
    ///     The gate singleton supplies the live count callbacks and holds the returned instruments for its lifetime.
    /// </summary>
    public static (ObservableGauge<long> Active, ObservableGauge<long> Waiting) CreateGpuModelLoadAdmissionGauges(
        Func<long> observeActive,
        Func<long> observeWaiting)
    {
        ArgumentNullException.ThrowIfNull(observeActive);
        ArgumentNullException.ThrowIfNull(observeWaiting);
        var active = Meter.CreateObservableGauge("gpu_admission_active",
            observeActive,
            unit: "loads",
            description: "Number of GPU-backed model loads currently holding the admission gate (0 or 1).");
        var waiting = Meter.CreateObservableGauge("gpu_admission_waiting",
            observeWaiting,
            unit: "loads",
            description: "Number of GPU-backed model loads currently waiting for the admission gate.");
        return (active, waiting);
    }
}
