namespace XE_Local_AI_Engine.Installer.Manifest;

/// <summary>
///     The subset of the runtime manifest the installer needs to derive teardown-ownership names
///     (plan §7.4 inventory). Parsed from the bundle's <c>manifest/managed.yaml</c>. Only the
///     container names matter for ownership attribution; the full manifest is consumed by the
///     in-distro reconciler, not by the installer process.
/// </summary>
public sealed record InstallerManifest
{
    public required IReadOnlyList<string> ContainerNames { get; init; }
}
