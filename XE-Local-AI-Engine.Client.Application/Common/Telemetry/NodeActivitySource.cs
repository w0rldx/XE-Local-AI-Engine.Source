namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using System.Diagnostics;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;

/// <summary>
///     The node's own <see cref="ActivitySource" /> for coarse, in-house spans around the pre-spawn stages of a chat
///     turn (turn resolution, runtime-package validation, model readiness). It exists to make the audited "silent
///     pre-spawn gap" (AUD4-23: a first-ever send stalled several seconds before the model spawn with zero log lines)
///     observable as timed spans rather than an apparent hang.
///     <para>
///         The source name is exported by <c>ServiceDefaults.ConfigureOpenTelemetry</c>. It deliberately mirrors
///         <see cref="NodeMetrics.MeterName" /> so the node's traces and metrics share one namespace. Spans are COARSE
///         (one per stage) and carry only low-cardinality tags — never prompt content, conversation ids, model names,
///         or user paths.
///     </para>
/// </summary>
public static class NodeActivitySource
{
    /// <summary>The exported source name.</summary>
    public const string SourceName = TelemetrySourceNames.Node;

    /// <summary>The shared source. A span is created only when a listener is attached (exporter configured), so the
    ///     instrumentation is free when telemetry is off (the desktop/RC default).</summary>
    public static readonly ActivitySource Source = new(SourceName);
}
