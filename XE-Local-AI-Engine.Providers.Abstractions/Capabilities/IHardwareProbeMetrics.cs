namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Seam for recording hardware-probe reliability signals. The profiler lives in the Providers.Capabilities layer,
///     which cannot reference the application layer's <c>NodeMetrics</c> meter directly (layering: providers depend only
///     on Abstractions). The host supplies a <c>NodeMetrics</c>-backed implementation; tests and headless hosts fall back
///     to <see cref="NullHardwareProbeMetrics" />.
/// </summary>
public interface IHardwareProbeMetrics
{
    /// <summary>
    ///     Records that a native hardware probe (e.g. <c>nvidia-smi</c>) exceeded its wall-clock deadline and was killed
    ///     (process tree). <paramref name="probe" /> is a low-cardinality tool name label — never a path or command line.
    /// </summary>
    void RecordProbeTimeout(string probe);
}

/// <summary>No-op <see cref="IHardwareProbeMetrics" /> — the default when no metrics sink is wired.</summary>
public sealed class NullHardwareProbeMetrics : IHardwareProbeMetrics
{
    /// <summary>The shared no-op instance.</summary>
    public static NullHardwareProbeMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordProbeTimeout(string probe)
    {
        // Intentionally does nothing — the null-object default.
    }
}
