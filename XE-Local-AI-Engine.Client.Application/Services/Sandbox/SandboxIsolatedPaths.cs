namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using System.Diagnostics.CodeAnalysis;

/// <summary>The paths a <see cref="SandboxIsolationMode.Filesystem" /> sandbox presents to the command it runs.</summary>
/// <remarks>
///     Part of the provider-neutral contract rather than of one provider's launch chain, because a CALLER has to name them: under isolation
///     a host path means nothing inside the namespace, so a caller composing an environment or a working directory spells the in-sandbox
///     view. A provider advertising <see cref="SandboxProviderCapabilities.SupportsFilesystemIsolation" /> materialises all three before
///     the command starts: <see cref="Work" /> is the single writable tree and IS the jail, which is what keeps a jail-occupancy watchdog
///     meaningful; <see cref="Home" /> and <see cref="Temp" /> are separate directories inside it, so clearing temp cannot wipe home.
/// </remarks>
public static class SandboxIsolatedPaths
{
    /// <summary>The sandbox's writable root and default working directory.</summary>
    public const string Work = "/work";

    /// <summary><c>HOME</c> inside the sandbox. Under <see cref="Work" />, so what it accumulates is metered.</summary>
    public const string Home = "/work/home";

    // Not a host directory but a mount point INSIDE one sandbox's own namespace, backed by a private 0700 engine-owned jail subdirectory
    // no other process shares, so no publicly writable directory is involved. Every library reading TMPDIR fixes the name.
    [SuppressMessage("Security Hotspot",
        "S5443:Using publicly writable directories is security-sensitive",
        Justification = "In-namespace mount point backed by a private 0700 jail subdirectory, not a host directory.")]
    public const string Temp = "/tmp";
}
