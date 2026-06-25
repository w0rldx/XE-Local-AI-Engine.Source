namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     Resolves whether the process was started in "desktop" mode — a self-contained, double-click launch that binds
///     loopback HTTP, opens the default browser, and routes a closed console window into a graceful shutdown.
///     Desktop mode is opt-in via env <c>XE_LAUNCH_MODE=desktop</c> or CLI <c>--desktop</c>, and is also implied by a
///     Velopack-managed install (the caller passes <c>isManagedInstall</c>): the packaged installer/portable flavor IS
///     the desktop app — its in-app updater is desktop-only — yet the Velopack stub launches the bare exe without the
///     env/arg a manual launcher would set. Headless, Aspire, and CI runs are not Velopack installs and set none of
///     these signals, so they stay byte-behavior-unchanged.
/// </summary>
internal static class DesktopLaunch
{
    /// <summary>The environment variable a launcher sets to request desktop mode.</summary>
    internal const string LaunchModeEnvironmentVariable = "XE_LAUNCH_MODE";

    /// <summary>The value of <see cref="LaunchModeEnvironmentVariable" /> that selects desktop mode (case-insensitive).</summary>
    internal const string DesktopModeValue = "desktop";

    /// <summary>The CLI argument that selects desktop mode.</summary>
    internal const string DesktopArgument = "--desktop";

    /// <summary>
    ///     The loopback URL desktop mode binds (port 0 lets the OS pick a free port). Composed from parts rather than a
    ///     literal so it reads as a bind specification, not a fixed endpoint.
    /// </summary>
    internal const string LoopbackBindUrl = "http://" + LoopbackHost + ":0";

    /// <summary>The loopback host desktop mode binds exclusively (never a routable interface).</summary>
    internal const string LoopbackHost = "127.0.0.1";

    /// <summary>
    ///     Resolves desktop mode from the process arguments and an injected environment reader. The reader indirection
    ///     keeps this pure and unit-testable without mutating real process environment state.
    /// </summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="environmentReader">Resolves an environment variable by name; returns <c>null</c> when unset.</param>
    internal static bool IsDesktopMode(string[] args, Func<string, string?> environmentReader)
    {
        return IsDesktopMode(args, environmentReader, isManagedInstall: false);
    }

    /// <summary>
    ///     Resolves desktop mode from the process arguments, an injected environment reader, and whether the process is
    ///     running from a Velopack-managed install. The install signal is computed by the caller (it depends on Velopack)
    ///     and kept out of this type so the gate stays pure and unit-testable.
    /// </summary>
    /// <param name="args">The process command-line arguments.</param>
    /// <param name="environmentReader">Resolves an environment variable by name; returns <c>null</c> when unset.</param>
    /// <param name="isManagedInstall">
    ///     <see langword="true" /> when the process is running from a Velopack-managed install (installer or portable),
    ///     which is inherently the desktop flavor and therefore enters desktop mode without an explicit env/arg.
    /// </param>
    internal static bool IsDesktopMode(string[] args, Func<string, string?> environmentReader, bool isManagedInstall)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environmentReader);

        // A Velopack-managed install is always the desktop flavor; the stub launches the bare exe with no env/arg, so the
        // install itself is the opt-in signal. Off this flag the env/arg checks below preserve the original behavior.
        if (isManagedInstall)
        {
            return true;
        }

        var launchMode = environmentReader(LaunchModeEnvironmentVariable);
        if (string.Equals(launchMode, DesktopModeValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return args.Any(static arg => string.Equals(arg, DesktopArgument, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Convenience overload that reads from the real process environment and takes the caller-computed Velopack
    ///     install signal. Used by <c>Program.cs</c>; tests call the injectable overload above.
    /// </summary>
    internal static bool IsDesktopMode(string[] args, bool isManagedInstall)
    {
        return IsDesktopMode(args, Environment.GetEnvironmentVariable, isManagedInstall);
    }
}
