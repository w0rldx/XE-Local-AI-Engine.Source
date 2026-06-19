namespace XE_Local_AI_Engine.Client.Hosting;

/// <summary>
///     Resolves whether the process was started in "desktop" mode — a self-contained, double-click launch that binds
///     loopback HTTP, opens the default browser, and routes a closed console window into a graceful shutdown.
///     Desktop mode is strictly opt-in (env <c>XE_LAUNCH_MODE=desktop</c> or CLI <c>--desktop</c>) so that headless,
///     Aspire, and CI runs are byte-behavior-unchanged.
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
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environmentReader);

        var launchMode = environmentReader(LaunchModeEnvironmentVariable);
        if (string.Equals(launchMode, DesktopModeValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return args.Any(static arg => string.Equals(arg, DesktopArgument, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Convenience overload that reads from the real process environment. Used by <c>Program.cs</c>; tests call the
    ///     injectable overload above.
    /// </summary>
    internal static bool IsDesktopMode(string[] args)
    {
        return IsDesktopMode(args, Environment.GetEnvironmentVariable);
    }
}
