namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;

using System.Diagnostics.Metrics;

/// <summary>
///     AI.Agent-layer counters for the orchestration idle watchdog. Emitted here rather than on the application layer's
///     <c>NodeMetrics</c> because <c>OrchestrationRunSession</c> lives below that layer (the layer arrow forbids the
///     reference); they mirror the application-layer chat-stream watchdog/abandonment counters on the existing
///     <c>XE.LocalAiEngine.AI.Agent</c> meter. Content-free — counts only.
/// </summary>
internal static class OrchestrationMetrics
{
    private static readonly Meter Meter = new("XE.LocalAiEngine.AI.Agent", "1.0.0");

    private static readonly Counter<long> WatchdogTimeoutCounter =
        Meter.CreateCounter<long>("xe.agent.orchestration.watchdog_timeout",
            description: "Orchestration runs stopped by the inter-event idle watchdog.");

    private static readonly Counter<long> ProviderAbandonedCounter =
        Meter.CreateCounter<long>("xe.agent.orchestration.provider_abandoned",
            description: "Stalled orchestration workflows abandoned after ignoring cancellation within the watchdog grace.");

    /// <summary>Records that the orchestration idle watchdog fired (no productive event within the idle window).</summary>
    public static void RecordWatchdogTimeout()
    {
        WatchdogTimeoutCounter.Add(1);
    }

    /// <summary>Records that a stalled workflow advancement or disposal was abandoned after ignoring cancellation.</summary>
    public static void RecordAbandoned()
    {
        ProviderAbandonedCounter.Add(1);
    }
}
