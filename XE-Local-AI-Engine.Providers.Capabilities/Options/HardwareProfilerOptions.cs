namespace XE_Local_AI_Engine.Providers.Capabilities.Options;

/// <summary>
///     Caller-supplied, provider-neutral configuration for <see cref="HardwareProfiler" />. The host injects the
///     models/content-root path whose volume the free-disk figure is reported for, so no platform-specific default
///     leaks into the profiler.
/// </summary>
public sealed record HardwareProfilerOptions
{
    private readonly int _hardwareProbeTimeoutSeconds = 5;

    /// <summary>
    ///     Path on the models/content-root volume used to report <see cref="Abstractions.Capabilities.HardwareProfile.FreeDiskBytes" />.
    ///     Defaults to the process working directory.
    /// </summary>
    public string ModelsVolumePath { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    ///     Wall-clock deadline in seconds for each native hardware process probe, such as <c>nvidia-smi</c>. Defaults
    ///     to 5 s and must be positive.
    /// </summary>
    /// <remarks>
    ///     On overrun the probe is killed as a process tree and the profiler degrades to the last cached profile or the
    ///     CPU-safe default: a wedged GPU driver must never hang first-run provisioning or a capacity decision.
    /// </remarks>
    public int HardwareProbeTimeoutSeconds
    {
        get => _hardwareProbeTimeoutSeconds;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _hardwareProbeTimeoutSeconds = value;
        }
    }
}
