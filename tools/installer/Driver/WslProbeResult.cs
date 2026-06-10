namespace XE_Local_AI_Engine.Installer.Driver;

/// <summary>
///     What <see cref="IInstallerEnvironmentDriver.ProbeAsync" /> reports about the host before any
///     mutation (plan §7.5 <c>probe</c>). Drives the preflight gates: WSL2 capability, the
///     distro-collision abort (MED-7b), and free-disk (MED-7a).
/// </summary>
public sealed record WslProbeResult
{
    /// <summary>True once the WSL2 feature is present (so <c>wsl-enable</c> self-skips).</summary>
    public required bool WslFeaturePresent { get; init; }

    /// <summary>True when the host meets the minimum Windows build + WSL2 capability gate (MED-7d).</summary>
    public required bool Wsl2Capable { get; init; }

    /// <summary>True when the <c>xe-engine-runtime</c> distro is already registered.</summary>
    public required bool DistroPresent { get; init; }

    /// <summary>Free disk available (bytes) on the install target volume, for the MED-7a check.</summary>
    public required long FreeDiskBytes { get; init; }

    /// <summary>
    ///     Minimum free disk the bundle requires (bytes), read from <c>bundle-metadata.json</c>'s
    ///     <c>minimumFreeDiskBytes</c> (MED-7a / code#2). The driver derives it from the bundle rather
    ///     than a hardcoded constant so the requirement tracks the actual rootfs + image + model sizes.
    /// </summary>
    public required long RequiredFreeDiskBytes { get; init; }
}
