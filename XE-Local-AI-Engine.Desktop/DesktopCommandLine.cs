namespace XE_Local_AI_Engine.Desktop;

internal static class DesktopCommandLine
{
    /// <summary>The engine's launch-mode environment variable; the engine duplicates both literals.</summary>
    internal const string LaunchModeVariable = "XE_LAUNCH_MODE";

    internal const string McpOnlyModeValue = "mcp-only";

    internal static bool RunsEngine(string[] args) =>
        RunsEngine(args, Environment.GetEnvironmentVariable(LaunchModeVariable));

    /// <summary>True when the process must run the engine without a window: an explicit browser, headless, operator or
    ///     owned-engine argument, or an unattended <c>XE_LAUNCH_MODE=mcp-only</c> that no explicit mode argument overrides.</summary>
    internal static bool RunsEngine(string[] args, string? launchMode)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument => Is(argument, "--browser") || Is(argument, "--headless") || Is(argument, "--mcp-only")
                                    || Is(argument, "--help") || Is(argument, "--status") || Is(argument, "--setup") || Is(argument, "--mcp-key")
                                    || argument.StartsWith("--mcp-key=", StringComparison.OrdinalIgnoreCase)
                                    || Is(argument, "--reset-admin-password") || Is(argument, "--knowledge-downgrade-preflight") || Is(argument, "--knowledge-downgrade-export"))
               || args.Any(argument => Is(argument, "--no-browser"))
               || (IsMcpOnly(launchMode) && !args.Any(argument => Is(argument, "--desktop")));
    }

    internal static string[] EngineArguments(string[] args) =>
        EngineArguments(args, Environment.GetEnvironmentVariable(LaunchModeVariable));

    internal static string[] EngineArguments(string[] args, string? launchMode)
    {
        ArgumentNullException.ThrowIfNull(args);
        var browser = args.Any(argument => Is(argument, "--browser"));
        var headless = args.Any(argument => Is(argument, "--headless"));
        if (browser && headless)
        {
            throw new ArgumentException("Choose either browser or headless mode.", nameof(args));
        }

        var forwarded = args.ToList();
        // An environment-selected MCP-only run is left to the engine's own resolver: adding --desktop would override it.
        if (!browser && !headless && !IsMcpOnly(launchMode)
            && !forwarded.Any(argument => Is(argument, "--desktop") || Is(argument, "--mcp-only")))
        {
            forwarded.Add("--desktop");
        }

        return [.. forwarded];
    }

    private static bool IsMcpOnly(string? launchMode) =>
        Is(launchMode ?? string.Empty, McpOnlyModeValue);

    private static bool Is(string value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
