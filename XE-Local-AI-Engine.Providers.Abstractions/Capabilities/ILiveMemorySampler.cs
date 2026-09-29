namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Reads the host's current whole-machine RAM and per-GPU VRAM usage for a live gauge, separate from the cached
///     <see cref="IHardwareProfiler" /> profile.
/// </summary>
/// <remarks>
///     Implementations share one reading for a short window so several pollers cost one probe, never refresh or
///     overwrite the profiler's cache, and never throw for a missing GPU or tool: unknown VRAM is an empty device list.
/// </remarks>
public interface ILiveMemorySampler
{
    /// <summary>Returns the current memory sample, probing only when the shared reading has expired.</summary>
    Task<LiveMemorySample> SampleAsync(CancellationToken ct);
}
