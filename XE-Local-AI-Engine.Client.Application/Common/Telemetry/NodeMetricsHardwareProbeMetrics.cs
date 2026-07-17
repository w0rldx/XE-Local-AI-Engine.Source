namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     <see cref="NodeMetrics" />-backed <see cref="IHardwareProbeMetrics" />. Bridges the Providers.Capabilities probe
///     seam (which cannot reference the application-layer meter) to the shared <c>XE.Node</c> meter, incrementing
///     <see cref="NodeMetrics.HardwareProbeTimeoutTotal" /> when a native hardware probe is killed for overrunning its
///     deadline. Registered by the host so the profiler emits the counter; tests/headless hosts use the null default.
/// </summary>
internal sealed class NodeMetricsHardwareProbeMetrics : IHardwareProbeMetrics
{
    /// <inheritdoc />
    public void RecordProbeTimeout(string probe)
    {
        NodeMetrics.HardwareProbeTimeoutTotal.Add(1, new KeyValuePair<string, object?>("probe", probe));
    }
}
