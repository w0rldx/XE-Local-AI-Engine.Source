namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>No-op <see cref="IHardwareProbeMetrics" /> — the default when no metrics sink is wired.</summary>
public sealed class NullHardwareProbeMetrics : IHardwareProbeMetrics
{
    public static NullHardwareProbeMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordProbeTimeout(string probe)
    {
    }
}
