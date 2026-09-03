namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     GPU vendor reported by <see cref="IHardwareProfiler" />. Provider-neutral so the advisor can pick a memory-fit
///     budget without depending on any concrete runtime or platform.
/// </summary>
public enum GpuVendor
{
    /// <summary>Vendor could not be determined (probe ran but yielded no recognizable adapter signature).</summary>
    Unknown = 0,

    /// <summary>No GPU adapter detected — CPU-only floor.</summary>
    None = 1,

    Nvidia = 2,

    Amd = 3,

    Intel = 4
}
