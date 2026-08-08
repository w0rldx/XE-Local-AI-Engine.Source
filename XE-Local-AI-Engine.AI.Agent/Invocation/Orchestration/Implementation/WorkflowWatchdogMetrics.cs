namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;

using System.Diagnostics.Metrics;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;

/// <summary>
///     Shared AI.Agent-layer counters for the workflow idle watchdog, emitted by both the orchestration and the Preview
///     run sessions and distinguished by a <c>surface</c> tag rather than by separate instruments (keeps the instrument
///     count low and the convention uniform). Emitted here rather than on the application layer's <c>NodeMetrics</c>
///     because these sessions live below that layer (the layer arrow forbids the reference); they sit on the existing
///     exported <c>XE.LocalAiEngine.AI.Agent</c> meter. Content-free — counts only. Note a Preview run has no inter-event
///     idle deadline, so it only ever emits the abandonment counter, never the watchdog-timeout one.
/// </summary>
internal static class WorkflowWatchdogMetrics
{
    /// <summary><c>surface</c> tag value for the handoff orchestration run session.</summary>
    public const string OrchestrationSurface = "orchestration";

    /// <summary><c>surface</c> tag value for the Preview workflow run session.</summary>
    public const string PreviewSurface = "preview";

    private const string SurfaceTag = "surface";

    private static readonly Meter Meter = new(TelemetrySourceNames.Agent, "1.0.0");

    private static readonly Counter<long> WatchdogTimeoutCounter =
        Meter.CreateCounter<long>("xe.agent.workflow.watchdog_timeout",
            description: "Workflow runs stopped by the inter-event idle watchdog. Tag: surface.");

    private static readonly Counter<long> ProviderAbandonedCounter =
        Meter.CreateCounter<long>("xe.agent.workflow.provider_abandoned",
            description: "Stalled workflows abandoned after ignoring cancellation within the watchdog grace. Tag: surface.");

    /// <summary>Records that a workflow idle watchdog fired (no productive event within the idle window) for <paramref name="surface" />.</summary>
    public static void RecordWatchdogTimeout(string surface)
    {
        WatchdogTimeoutCounter.Add(1, new KeyValuePair<string, object?>(SurfaceTag, surface));
    }

    /// <summary>Records that a stalled workflow advancement or disposal was abandoned after ignoring cancellation, for <paramref name="surface" />.</summary>
    public static void RecordAbandoned(string surface)
    {
        ProviderAbandonedCounter.Add(1, new KeyValuePair<string, object?>(SurfaceTag, surface));
    }
}
