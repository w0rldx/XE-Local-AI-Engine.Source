namespace XE_Local_AI_Engine.Installer.Manifest;

/// <summary>
///     Ownership rule for teardown attribution — a faithful mirror of
///     <c>XE_Local_AI_Engine.HostAgent.Linux.Lifecycle.ContainerOwnership.Owns</c> (plan §3 invariant 1).
///     The installer project is standalone (not in the solution graph, no HostAgent.Linux reference),
///     so the rule is replicated here rather than referenced. The stance is FAIL-CLOSED: a null
///     manifest owns NOTHING, and the match is an ordinal name comparison against the declared
///     containers. A deletion the installer cannot attribute here (or to a documented fixed path)
///     must NOT happen.
/// </summary>
public static class InstallerContainerOwnership
{
    public static bool Owns(InstallerManifest? manifest, string containerName)
    {
        if (manifest is null)
        {
            return false;
        }

        return manifest.ContainerNames.Any(declared => string.Equals(declared, containerName, StringComparison.Ordinal));
    }
}
