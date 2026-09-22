namespace XE_Local_AI_Engine.Desktop;

internal static class DesktopCommandLine
{
    internal static bool RunsEngine(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument => Is(argument, "--browser") || Is(argument, "--headless") || Is(argument, "--mcp-only")
            || Is(argument, "--help") || Is(argument, "--status") || Is(argument, "--setup") || Is(argument, "--mcp-key")
            || argument.StartsWith("--mcp-key=", StringComparison.OrdinalIgnoreCase)
            || Is(argument, "--reset-admin-password") || Is(argument, "--knowledge-downgrade-preflight") || Is(argument, "--knowledge-downgrade-export"))
            || args.Any(argument => Is(argument, "--no-browser"));
    }

    internal static string[] EngineArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var browser = args.Any(argument => Is(argument, "--browser"));
        var headless = args.Any(argument => Is(argument, "--headless"));
        if (browser && headless)
        {
            throw new ArgumentException("Choose either browser or headless mode.", nameof(args));
        }

        var forwarded = args.ToList();
        if (!browser && !headless && !forwarded.Any(argument => Is(argument, "--desktop") || Is(argument, "--mcp-only")))
        {
            forwarded.Add("--desktop");
        }

        return [.. forwarded];
    }

    private static bool Is(string value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
