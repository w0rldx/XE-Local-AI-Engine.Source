namespace XE_Local_AI_Engine.Installer.StateMachine;

/// <summary>
///     Immutable inputs the install state machine needs that are not OS actions: the bundle path,
///     the installer version stamped into the manifest, the bootstrap model name, the distro name,
///     and the minimum free-disk requirement derived from payload sizes (plan §7.5 probe / MED-7a).
/// </summary>
public sealed record InstallContext
{
    public required string BundlePath { get; init; }

    public required string InstallerVersion { get; init; }

    public required string DistroName { get; init; }

    public required string BootstrapModel { get; init; }

    public required long MinimumFreeDiskBytes { get; init; }
}
