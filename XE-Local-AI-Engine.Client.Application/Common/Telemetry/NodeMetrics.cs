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
}
