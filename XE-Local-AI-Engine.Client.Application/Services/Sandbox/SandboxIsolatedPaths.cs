namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     The paths a <see cref="SandboxIsolationMode.Filesystem" /> sandbox presents to the command it runs. Part of the
///     provider-neutral contract rather than of any one provider's launch chain, because a CALLER has to be able to
///     name them: under isolation a host path means nothing inside the namespace, so a caller composing an environment
///     or a working directory has to spell the in-sandbox view.
///     <para>
///         A provider that advertises <see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" /> undertakes
///         to materialize all three before the command starts: <see cref="Work" /> is the sandbox's single writable
///         tree (it IS the jail, which is what keeps a jail-occupancy watchdog meaningful), <see cref="Home" /> is a
///         directory inside it, and <see cref="Temp" /> is a second one — separate, because a script clearing its temp
///         files must not wipe its own home.
///     </para>
/// </summary>
public static class SandboxIsolatedPaths
{
    /// <summary>The sandbox's writable root and default working directory.</summary>
    public const string Work = "/work";

    /// <summary><c>HOME</c> inside the sandbox. Under <see cref="Work" />, so what it accumulates is metered.</summary>
    public const string Home = "/work/home";

    // Not a host directory. This is a mount point INSIDE one sandbox's own mount namespace, backed by a private 0700
    // engine-owned jail subdirectory no other process on the box shares — so there is no publicly writable directory
    // here to avoid. The name is fixed by every library that reads TMPDIR, so "use a different directory" is not on
    // the table.
    [SuppressMessage("Security Hotspot",
        "S5443:Using publicly writable directories is security-sensitive",
        Justification = "In-namespace mount point backed by a private 0700 jail subdirectory, not a host directory.")]
    public const string Temp = "/tmp";
}
