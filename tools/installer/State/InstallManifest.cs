namespace XE_Local_AI_Engine.Installer.State;

/// <summary>
///     Written atomically as the LAST install step (plan §6.1, <c>install-manifest.json</c>),
///     recording every created artifact. Its presence plus a matching <see cref="InstallerVersion" />
///     is the "already installed" detector; teardown reverses the recorded order.
/// </summary>
public sealed record InstallManifest
{
    public required string InstallerVersion { get; init; }

    public required string BundleSha256 { get; init; }

    public required string DistroName { get; init; }

    /// <summary>Loaded app-image identity (config digest / <c>Id</c>, §6.3) verified against the bundle.</summary>
    public required string AppImageId { get; init; }

    public required string PulledModel { get; init; }

    /// <summary>Fixed paths the install created, in creation order (teardown reverses).</summary>
    public required IReadOnlyList<string> CreatedPaths { get; init; }

    public required DateTimeOffset InstalledAtUtc { get; init; }
}
