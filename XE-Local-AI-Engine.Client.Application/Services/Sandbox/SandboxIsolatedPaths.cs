namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using System.Diagnostics.CodeAnalysis;

/// <summary>
///     The paths a <see cref="SandboxIsolationMode.Filesystem" /> sandbox presents to the command it runs, as the provider REPORTED them
///     for one created sandbox (<see cref="SandboxHandle.IsolatedPaths" />).
/// </summary>
/// <remarks>
///     Per sandbox, because the view depends on the serving mechanism: the Linux mount namespace presents <see cref="Posix" />, the
///     Windows AppContainer boundary has no namespace and presents the jail's host paths (<see cref="ForHostJail" />, ADR 0019).
///     <see cref="Work" /> is the single writable tree and IS the jail; <see cref="Home" /> and <see cref="Temp" /> are separate
///     directories inside it, so clearing temp cannot wipe home.
/// </remarks>
public sealed record SandboxIsolatedPaths
{
    /// <summary>The jail subdirectory that backs <see cref="Home" />.</summary>
    public const string HomeDirectoryName = "home";

    /// <summary>The jail subdirectory that backs <see cref="Temp" />; a sibling of the home, never inside it.</summary>
    public const string TempDirectoryName = ".tmp";

    /// <summary>The Linux mount-namespace writable root, where the isolated chain binds the jail.</summary>
    internal const string PosixWork = "/work";

    /// <summary>The Linux mount-namespace <c>HOME</c>: <see cref="HomeDirectoryName" /> under <see cref="PosixWork" />.</summary>
    internal const string PosixHome = PosixWork + "/" + HomeDirectoryName;

    // Not a host directory but a mount point INSIDE one sandbox's own namespace, backed by a private 0700 engine-owned jail subdirectory
    // no other process shares, so no publicly writable directory is involved. Every library reading TMPDIR fixes the name.
    [SuppressMessage("Security Hotspot",
        "S5443:Using publicly writable directories is security-sensitive",
        Justification = "In-namespace mount point backed by a private 0700 jail subdirectory, not a host directory.")]
    internal const string PosixTemp = "/tmp";

    /// <summary>The view the Linux isolated chain (bwrap) presents: <c>/work</c>, <c>/work/home</c>, <c>/tmp</c>.</summary>
    public static SandboxIsolatedPaths Posix { get; } = new()
    {
        Work = PosixWork,
        Home = PosixHome,
        Temp = PosixTemp
    };

    /// <summary>The sandbox's writable root and default working directory.</summary>
    public required string Work { get; init; }

    /// <summary><c>HOME</c> inside the sandbox. Under <see cref="Work" />, so what it accumulates is metered.</summary>
    public required string Home { get; init; }

    /// <summary><c>TMPDIR</c>/<c>TMP</c>/<c>TEMP</c> inside the sandbox, backed by a jail subdirectory.</summary>
    public required string Temp { get; init; }

    /// <summary>
    ///     The view of a boundary with no mount namespace (the Windows AppContainer boundary): the jail's own host paths.
    /// </summary>
    public static SandboxIsolatedPaths ForHostJail(string jailRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jailRoot);
        return new SandboxIsolatedPaths
        {
            Work = jailRoot,
            Home = Path.Combine(jailRoot, HomeDirectoryName),
            Temp = Path.Combine(jailRoot, TempDirectoryName)
        };
    }

    /// <summary>
    ///     The paths of an isolated sandbox, or <see langword="null" /> for one that is not isolated.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The handle reports <see cref="SandboxIsolationMode.Filesystem" /> but no paths: a provider bug, refused rather than guessed,
    ///     because a guessed <c>HOME</c> on the wrong platform names a directory the child cannot reach or one outside the jail.
    /// </exception>
    public static SandboxIsolatedPaths? Of(SandboxHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Isolation != SandboxIsolationMode.Filesystem)
        {
            return null;
        }

        return handle.IsolatedPaths
               ?? throw new InvalidOperationException(
                   $"The '{handle.ProviderName}' sandbox '{handle.SandboxId}' reports filesystem isolation but no isolated paths; the provider must report the view it created.");
    }

    /// <summary>
    ///     The scratch environment: <c>HOME</c>, <c>TMPDIR</c>, <c>TMP</c>, <c>TEMP</c>; on Windows also the profile variables, pointed at
    ///     <see cref="Home" /> so no tool reaches for the real profile.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        return ToEnvironment(OperatingSystem.IsWindows());
    }

    // The platform is a parameter so the Windows overlay is asserted on every host, not only on a Windows runner.
    internal IReadOnlyDictionary<string, string> ToEnvironment(bool includeWindowsProfile)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["TMPDIR"] = Temp,
            ["TMP"] = Temp,
            ["TEMP"] = Temp
        };
        if (includeWindowsProfile)
        {
            environment["USERPROFILE"] = Home;
            environment["APPDATA"] = Home;
            environment["LOCALAPPDATA"] = Home;
        }

        return environment;
    }
}
