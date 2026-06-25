namespace XE_Local_AI_Engine.Providers.Capabilities.Options;

/// <summary>
///     Caller-supplied configuration for <see cref="HardwareProfiler" />. Provider-neutral: the host injects the
///     models/content-root path whose volume the free-disk figure is reported for (no Linux-specific default leaks in,
///     unlike the deleted <c>HostAgentCapabilityOptions</c>).
/// </summary>
public sealed record HardwareProfilerOptions
{
    /// <summary>
    ///     Path on the models/content-root volume used to report <see cref="Abstractions.Capabilities.HardwareProfile.FreeDiskBytes" />.
    ///     Defaults to the process working directory.
    /// </summary>
    public string ModelsVolumePath { get; init; } = Directory.GetCurrentDirectory();
}
