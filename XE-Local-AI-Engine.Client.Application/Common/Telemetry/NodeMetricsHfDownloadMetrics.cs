namespace XE_Local_AI_Engine.Client.Common.Telemetry;

using XE_Local_AI_Engine.Providers.HuggingFace.Telemetry;

/// <summary>
///     <see cref="NodeMetrics" />-backed <see cref="IHfDownloadMetrics" />. Bridges the Providers.HuggingFace
///     download seam (which cannot reference the application-layer meter) to the shared <c>XE.Node</c> meter,
///     incrementing <see cref="NodeMetrics.DownloadReadTimeoutTotal" /> when a download body-copy loop stalls past its
///     read-idle timeout. Registered by the host so the download client emits the counter; tests/headless hosts use the
///     null default. Mirrors <see cref="NodeMetricsHardwareProbeMetrics" />.
/// </summary>
internal sealed class NodeMetricsHfDownloadMetrics : IHfDownloadMetrics
{
    /// <inheritdoc />
    public void RecordReadIdleTimeout()
    {
        NodeMetrics.DownloadReadTimeoutTotal.Add(1);
    }
}
