namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;

/// <summary>
///     Represents node metrics.
/// </summary>
public static class NodeMetrics
{
    public const string MeterName = TelemetrySourceNames.Node;

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
    ///     Incremented when a chat stream event could not be buffered because the stream's bounded queue was full.
    ///     Should stay at ZERO — the queue holds ~80 s of consumer lag at the live emit cadence — so a non-zero value
    ///     means a real consumer stall rather than a merely slow browser. Content-free (a count only).
    ///     Labels: reason (queue_capacity | queue_bytes).
    /// </summary>
    public static readonly Counter<long> ChatStreamEnqueueDroppedTotal =
        Meter.CreateCounter<long>("chat_stream_enqueue_dropped_total",
            description: "Number of chat stream events dropped because the stream's bounded queue was full, by which bound was reached.");

    /// <summary>
    ///     Incremented each time the server tells a client to resynchronize an in-flight turn (re-subscribe through
    ///     <c>ResumeMessage</c> and take a fresh snapshot). The reconcile is silent to the user by design, so this
    ///     counter is the only signal that it is happening.
    ///     Labels: reason (queue_overflow | replay_cap). The third cause — a delta offset gap — is detected on the
    ///     client and rides the frontend error-snapshot breadcrumb trail, not this meter.
    /// </summary>
    public static readonly Counter<long> ChatStreamReconcileTotal =
        Meter.CreateCounter<long>("chat_stream_reconcile_total",
            description: "Number of server-initiated chat stream resynchronizations, by cause.");

    /// <summary>
    ///     Runs currently in flight with NO client attached. Incremented when an invocation's last stream subscriber
    ///     goes away and decremented when one re-attaches or the run terminalizes. This is the "leases held with nobody
    ///     watching" gauge: a detached run still holds its collision slot and its llama-server process, so a value that
    ///     stays above zero is what a disconnect-grace regression looks like. Content-free (a count only).
    /// </summary>
    public static readonly UpDownCounter<long> ChatStreamDetachedInvocations =
        Meter.CreateUpDownCounter<long>("chat_stream_detached_invocations",
            description: "Number of in-flight invocations with no client stream attached.");

    /// <summary>
    ///     Incremented when the disconnect grace expires and a detached run is cancelled. Answers "is the grace
    ///     deadline actually firing", which the gauge above cannot — that falls back to zero whether the client
    ///     returned or the run was reaped. Content-free (a count only).
    /// </summary>
    public static readonly Counter<long> ChatDetachedInvocationReapedTotal =
        Meter.CreateCounter<long>("chat_detached_invocation_reaped_total",
            description: "Number of detached invocations cancelled after their disconnect grace expired.");

    /// <summary>
    ///     Incremented when an invocation fails before producing output.
    ///     Labels: source (failure category, e.g. timeout | agent_runtime | provider_unreachable | unexpected).
    /// </summary>
    public static readonly Counter<long> InvocationFailedTotal =
        Meter.CreateCounter<long>("invocation_failed_total",
            description: "Number of failed invocations by failure source.");

    /// <summary>
    ///     One terminal, content-free observation per admitted production agent invocation. Labels are deliberately
    ///     bounded to provider (local | remote | unknown), outcome (completed | cancelled | failed), and orchestration
    ///     (true | false), so benchmark and production-mode comparisons cannot create per-user/model/tool series.
    /// </summary>
    public static readonly Counter<long> AgentHarnessInvocationTotal =
        Meter.CreateCounter<long>("agent_harness_invocation_total",
            description: "Number of terminal agent-harness invocations by bounded provider, outcome, and orchestration dimensions.");

    public static readonly Histogram<double> AgentHarnessTotalDurationMs =
        Meter.CreateHistogram<double>("agent_harness_total_duration_ms",
            unit: "ms",
            description: "Duration from the available harness entry timestamp through the invocation runner terminal.");

    public static readonly Histogram<double> AgentHarnessPreRunDurationMs =
        Meter.CreateHistogram<double>("agent_harness_pre_run_duration_ms",
            unit: "ms",
            description: "Local-chat admission, context construction, and pre-run persistence duration before queueing.");

    public static readonly Histogram<double> AgentHarnessQueueDurationMs =
        Meter.CreateHistogram<double>("agent_harness_queue_duration_ms",
            unit: "ms",
            description: "Time a local-chat invocation waited for the collision/inference slot before starting.");

    public static readonly Histogram<double> AgentHarnessModelReadinessMs =
        Meter.CreateHistogram<double>("agent_harness_model_readiness_ms",
            unit: "ms",
            description: "Local model readiness duration within one production agent invocation, when applicable.");

    public static readonly Histogram<double> AgentHarnessFirstOutputMs =
        Meter.CreateHistogram<double>("agent_harness_first_output_ms",
            unit: "ms",
            description: "Time from the available harness entry timestamp to the first emitted reasoning or response chunk.");

    public static readonly Histogram<long> AgentHarnessProviderCalls =
        Meter.CreateHistogram<long>("agent_harness_provider_calls",
            unit: "calls",
            description: "Raw provider calls made by one production agent invocation.");

    public static readonly Histogram<long> AgentHarnessEstimatedInputTokens =
        Meter.CreateHistogram<long>("agent_harness_estimated_input_tokens",
            unit: "tokens",
            description: "Cumulative estimated provider-input tokens sent by one production agent invocation.");

    public static readonly Histogram<long> AgentHarnessReportedInputTokens =
        Meter.CreateHistogram<long>("agent_harness_reported_input_tokens",
            unit: "tokens",
            description: "Terminal provider-reported input tokens for one production agent invocation, when available.");

    public static readonly Histogram<long> AgentHarnessReportedOutputTokens =
        Meter.CreateHistogram<long>("agent_harness_reported_output_tokens",
            unit: "tokens",
            description: "Terminal provider-reported output tokens for one production agent invocation, when available.");

    public static readonly Histogram<long> AgentHarnessToolSchemaTokens =
        Meter.CreateHistogram<long>("agent_harness_tool_schema_tokens",
            unit: "tokens",
            description: "Cumulative estimated tool-schema tokens repeated across provider calls in one invocation.");

    public static readonly Histogram<double> AgentHarnessProviderRoundElapsedMs =
        Meter.CreateHistogram<double>("agent_harness_provider_round_elapsed_ms",
            unit: "ms",
            description: "Cumulative provider-round elapsed time, including stream backpressure, for one invocation.");

    public static readonly Histogram<long> AgentHarnessToolCalls =
        Meter.CreateHistogram<long>("agent_harness_tool_calls",
            unit: "calls",
            description: "Logical tool calls requested by one production agent invocation.");

    public static readonly Histogram<double> AgentHarnessToolRequestToResultMs =
        Meter.CreateHistogram<double>("agent_harness_tool_request_to_result_ms",
            unit: "ms",
            description: "Cumulative latency from first observed tool request fragment to its result in one invocation.");

    public static readonly Histogram<long> AgentHarnessToolResultBytes =
        Meter.CreateHistogram<long>("agent_harness_tool_result_bytes",
            unit: "By",
            description: "Cumulative serialized tool-result bytes returned during one invocation.");

    public static readonly Histogram<long> AgentHarnessProviderRetries =
        Meter.CreateHistogram<long>("agent_harness_provider_retries",
            unit: "retries",
            description: "Pre-first-output provider retries performed during one invocation.");

    public static readonly Histogram<long> AgentHarnessToolArgumentRepairs =
        Meter.CreateHistogram<long>("agent_harness_tool_argument_repairs",
            unit: "repairs",
            description: "Deterministic tool-argument repairs performed during one invocation.");

    public static readonly Histogram<long> AgentHarnessHandoffs =
        Meter.CreateHistogram<long>("agent_harness_handoffs",
            unit: "handoffs",
            description: "Agent-participant handoffs observed during one orchestration invocation.");

    public static readonly Histogram<long> AgentHarnessMessagesDropped =
        Meter.CreateHistogram<long>("agent_harness_messages_dropped",
            unit: "messages",
            description: "Conversation messages deterministically dropped at provider boundaries during one invocation.");

    public static readonly Histogram<long> AgentHarnessToolResultsTruncated =
        Meter.CreateHistogram<long>("agent_harness_tool_results_truncated",
            unit: "results",
            description: "Oversized tool results deterministically truncated at provider boundaries during one invocation.");

    public static readonly Histogram<double> AgentHarnessFirstToolRequestMs =
        Meter.CreateHistogram<double>("agent_harness_first_tool_request_ms",
            unit: "ms",
            description: "Time from the root harness scope starting to the first logical tool request.");

    /// <summary>
    ///     Incremented each time a model-invoked MCP tool call exceeds its per-call
    ///     <c>Mcp:ToolCallTimeoutSeconds</c> deadline and is cancelled, returning a typed tool-failure result to the model
    ///     (the run continues; the call is never retried). A non-zero rate flags a slow or wedged MCP server. Content-free
    ///     — a count only; carries no tool name or arguments.
    /// </summary>
    public static readonly Counter<long> McpToolTimeoutTotal =
        Meter.CreateCounter<long>("mcp_tool_timeout_total",
            description: "Number of MCP tool calls cancelled after exceeding their per-call timeout.");

    /// <summary>
    ///     Incremented once per RESOLVED tool-approval decision. Tagged by <c>category</c> (the tool's
    ///     <c>ToolCategory</c> name) and <c>decision</c> (approve | deny | timeout). Content-free — a count only; it
    ///     carries no tool arguments, message content, or ids. A rising deny/timeout rate flags a policy that is prompting
    ///     more than operators will accept.
    /// </summary>
    public static readonly Counter<long> ToolApprovalDecisionsTotal =
        Meter.CreateCounter<long>("tool_approval_decisions_total",
            description: "Number of resolved tool-approval decisions by category and decision.");

    /// <summary>
    ///     Incremented each time a Hugging Face model download's body-copy loop stalls longer than the configured
    ///     read-idle timeout and is cancelled (surfaced as a transient failure the resume/retry path re-attempts). A
    ///     non-zero rate flags a CDN that accepts the connection and then stops sending data mid-body. Content-free — a
    ///     count only; carries no URL, repo, or file name. Bridged from the Providers.HuggingFace layer via
    ///     <c>IHfDownloadMetrics</c> (which cannot reference this meter).
    /// </summary>
    public static readonly Counter<long> DownloadReadTimeoutTotal =
        Meter.CreateCounter<long>("download_read_timeout_total",
            description: "Number of Hugging Face download body reads cancelled after exceeding the read-idle timeout.");

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
    ///     Incremented when the runtime device audit detects a silent CPU fallback: the host advertises a GPU
    ///     but the SELECTED llama.cpp runtime cannot use it — a CPU variant was chosen on a GPU box, or a GPU variant
    ///     enumerated zero devices (e.g. the shipped Vulkan build under WSL2 with no Vulkan ICD). Fires once per detection
    ///     (the audit is cached per binary), so a non-zero value flags inference silently running on the CPU while the
    ///     UI/advisor sized models to VRAM. Labels: reason (cpu_variant | zero_devices).
    /// </summary>
    public static readonly Counter<long> DeviceFallbackTotal =
        Meter.CreateCounter<long>("device_fallback_total",
            description: "Number of detected silent CPU fallbacks (GPU expected but the selected runtime runs on the CPU), by reason.");

    /// <summary>
    ///     Spawn-through-readiness duration for an actual llama-server child attempt. Labels are bounded to role,
    ///     variant, and outcome (ready | failed | cancelled). This is distinct from
    ///     <see cref="ModelReadinessDurationMs" />, which measures the invocation's entire warm/reuse phase.
    /// </summary>
    public static readonly Histogram<double> LlamaServerLoadReadinessDurationMs =
        Meter.CreateHistogram<double>("llama_server_load_readiness_duration_ms",
            unit: "ms",
            description: "Duration of one llama-server spawn-through-readiness attempt, by role, variant, and outcome.");

    /// <summary>
    ///     One terminal observation per llama-server load attempt. Labels are bounded enums: role, variant, outcome,
    ///     placement (cpu | full | partial | unknown), attempt (primary | safe_retry), and speculation class. It carries no
    ///     model name, path, arguments, prompt, or runtime hash and is report-only.
    /// </summary>
    public static readonly Counter<long> LlamaServerLoadTotal =
        Meter.CreateCounter<long>("llama_server_load_total",
            description: "Number of llama-server load attempts by bounded readiness, placement, candidate-attempt, and speculation dimensions.");

    /// <summary>
    ///     Wall-clock duration (milliseconds) a GPU-backed model load waited to acquire the process-wide GPU-load
    ///     admission gate before its spawn began. Near-zero under no contention; a large value means a load
    ///     queued behind another load's spawn-through-readiness window. Content-free (a duration only).
    /// </summary>
    public static readonly Histogram<double> GpuModelLoadAdmissionWaitMs =
        Meter.CreateHistogram<double>("gpu_admission_wait_ms",
            unit: "ms",
            description: "Time a GPU-backed model load waited for the process-wide GPU-load admission gate before spawning.");

    /// <summary>
    ///     Incremented when a GPU-load admission wait exceeded its bounded max-wait and surfaced a typed timeout rather
    ///     than hanging a chat turn. A non-zero value flags a wedged load holding the gate past the backstop.
    ///     Content-free — a count only.
    /// </summary>
    public static readonly Counter<long> GpuModelLoadAdmissionTimeoutTotal =
        Meter.CreateCounter<long>("gpu_admission_timeout_total",
            description: "Number of GPU-load admission waits that exceeded the bounded max-wait and surfaced a typed timeout.");


    /// <summary>
    ///     Wall-clock latency (milliseconds) from the start of an invocation turn to the moment the local model load
    ///     begins — the audited "silent pre-spawn gap" (a first-ever send stalled ~7.8 s here with no log). Only
    ///     recorded for a local llama.cpp turn (the one that pays a cold load); cloud/Ollama turns never reach the warm
    ///     phase. Tagged by <c>provider</c> (local). Content-free (a duration only).
    /// </summary>
    public static readonly Histogram<double> TurnToModelLoadStartMs =
        Meter.CreateHistogram<double>("turn_to_model_load_start_ms",
            unit: "ms",
            description: "Latency from invocation-turn start to the local model load beginning, by provider.");

    /// <summary>
    ///     Wall-clock latency (milliseconds) from the model becoming READY (the local warm phase finished, or turn start
    ///     for a runtime with no cold-load) to the FIRST streamed output chunk — time-to-first-token. Tagged by
    ///     <c>provider</c> (local | remote); <c>remote</c> covers cloud and non-warming local runtimes (Ollama), whose
    ///     first-token latency is the provider's own rather than a local cold load. Content-free (a duration only).
    /// </summary>
    public static readonly Histogram<double> ModelReadyToFirstOutputMs =
        Meter.CreateHistogram<double>("model_ready_to_first_output_ms",
            unit: "ms",
            description: "Time from model-ready to the first streamed output chunk (TTFT), by provider (local | remote).");

    /// <summary>
    ///     Incremented once per cancelled invocation, tagged by <c>category</c> — <c>user</c> (an explicit user cancel),
    ///     <c>watchdog</c> (the invocation-level timeout fired), <c>operator_eject</c> (the model was force-ejected out
    ///     from under the turn), or <c>shutdown</c> (host shutdown cancelled the run). Distinct from
    ///     <see cref="InvocationFailedTotal" />, which deliberately EXCLUDES cancellations: a cancel is an outcome, not a
    ///     failure, so it is counted here with its cause. Content-free (a count only), bounded cardinality.
    /// </summary>
    public static readonly Counter<long> InvocationCancelledTotal =
        Meter.CreateCounter<long>("invocation_cancelled_total",
            description: "Number of cancelled invocations by category (user | watchdog | operator_eject | shutdown).");

    /// <summary>
    ///     Incremented (by the reported token count) at the terminal usage-finalize of an invocation turn, so cumulative
    ///     model token consumption — the real cost surface for cloud providers — is observable live without waiting on the
    ///     persisted <c>agent_execution_logs</c> ledger. Fires once per turn per direction (never per tool-loop round, so
    ///     it cannot double-count). Labels: provider (local | remote — the coarse routing dimension; remote covers cloud
    ///     and Ollama), model (the resolved model id), direction (input | output). Content-free — a token count only;
    ///     never any prompt, completion, or transcript text. Bounded cardinality (installed/cloud model ids are a small,
    ///     stable set).
    /// </summary>
    public static readonly Counter<long> ModelTokenUsageTotal =
        Meter.CreateCounter<long>("model_token_usage_total",
            description: "Model tokens consumed at invocation-turn finalize, by provider (local | remote), model, and direction (input | output).");

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
    ///     Bytes reclaimed by the query-embedding cache's eviction passes. Read against the lookup hit ratio: sustained
    ///     eviction with a falling hit ratio means the cache is thrashing its budget rather than serving repeats.
    ///     Content-free — a byte count only.
    /// </summary>
    public static readonly Counter<long> KnowledgeQueryEmbeddingCacheEvictedBytesTotal =
        Meter.CreateCounter<long>("knowledge_query_embedding_cache_evicted_bytes_total",
            unit: "By",
            description: "Bytes reclaimed by knowledge-search query-embedding cache evictions.");

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
    ///     process-wide admission gate (0 or 1 — the gate is a serializer) and how many are WAITING behind it.
    ///     The gate singleton supplies the live count callbacks and holds the returned instruments for its lifetime.
    /// </summary>
    public static GpuAdmissionGauges CreateGpuModelLoadAdmissionGauges(Func<long> observeActive,
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
        return new GpuAdmissionGauges(active, waiting);
    }
}

/// <summary>
///     The pair of observable gauges registered for the GPU model-load admission gate. The gate singleton holds them for
///     its lifetime — an unreferenced <see cref="ObservableGauge{T}" /> stops being observed.
/// </summary>
public sealed record GpuAdmissionGauges(ObservableGauge<long> Active, ObservableGauge<long> Waiting);
