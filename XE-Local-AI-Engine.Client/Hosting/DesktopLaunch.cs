namespace XE_Local_AI_Engine.Client.Hosting;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>Resolves local launch modes and parses the small operator CLI surface.</summary>
internal static class DesktopLaunch
{
    internal const string LaunchModeEnvironmentVariable = "XE_LAUNCH_MODE";
    internal const string AdminEmailEnvironmentVariable = "XE_ADMIN_EMAIL";
    internal const string AdminPasswordEnvironmentVariable = "XE_ADMIN_PASSWORD";
    internal const string DesktopModeValue = "desktop";
    internal const string McpOnlyModeValue = "mcp-only";
    internal const string DesktopArgument = "--desktop";
    internal const string McpOnlyArgument = "--mcp-only";
    internal const string NoBrowserArgument = "--no-browser";
    internal const string PortArgument = "--port";
    internal const string SetupArgument = "--setup";
    internal const string AdminEmailArgument = "--admin-email";
    internal const string AdminPasswordArgument = "--admin-password";
    internal const string AdminPasswordStdinArgument = "--admin-password-stdin";
    internal const string McpKeyArgument = "--mcp-key";
    internal const string StatusArgument = "--status";
    internal const string JsonArgument = "--json";
    internal const string HelpArgument = "--help";
    internal const string ResetAdminPasswordArgument = "--reset-admin-password";
    internal const string KnowledgeDowngradePreflightArgument = "--knowledge-downgrade-preflight";
    internal const string KnowledgeDowngradeExportArgument = "--knowledge-downgrade-export";
    internal const string LoopbackBindUrl = "http://" + LoopbackHost + ":0";
    internal const string LoopbackHost = "127.0.0.1";

    internal static LaunchMode ResolveLaunchMode(string[] args, Func<string, string?> environmentReader, bool isManagedInstall)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environmentReader);

        if (HasArgument(args, McpOnlyArgument))
        {
            return LaunchMode.McpOnly;
        }

        if (HasArgument(args, DesktopArgument))
        {
            return LaunchMode.Desktop;
        }

        var configured = environmentReader(LaunchModeEnvironmentVariable);
        if (string.Equals(configured, McpOnlyModeValue, StringComparison.OrdinalIgnoreCase))
        {
            return LaunchMode.McpOnly;
        }

        if (string.Equals(configured, DesktopModeValue, StringComparison.OrdinalIgnoreCase) || isManagedInstall)
        {
            return LaunchMode.Desktop;
        }

        return LaunchMode.Headless;
    }

    internal static LaunchMode ResolveLaunchMode(string[] args, bool isManagedInstall) =>
        ResolveLaunchMode(args, Environment.GetEnvironmentVariable, isManagedInstall);

    internal static bool HasExplicitLocalModeArgument(string[] args) =>
        HasArgument(args, McpOnlyArgument) || HasArgument(args, DesktopArgument);

    internal static bool HasNoBrowserFlag(string[] args) =>
        HasArgument(args, NoBrowserArgument);

    internal static bool ShouldSuppressBrowser(LaunchMode launchMode, bool noBrowserRequested) =>
        launchMode == LaunchMode.McpOnly || noBrowserRequested;

    internal static bool HasStatusFlag(string[] args) =>
        HasArgument(args, StatusArgument);

    internal static bool HasJsonFlag(string[] args) =>
        HasArgument(args, JsonArgument);

    internal static bool HasHelpFlag(string[] args) =>
        HasArgument(args, HelpArgument);

    internal static bool HasOneShotCommand(string[] args) =>
        HasHelpFlag(args) || HasStatusFlag(args) || HasArgument(args, SetupArgument) || HasArgument(args, McpKeyArgument);

    internal static IReadOnlyList<string> BuildRestartArguments(string[] args, LaunchMode launchMode, int? port)
    {
        ArgumentNullException.ThrowIfNull(args);
        var sanitized = new List<string>(capacity: 4)
        {
            launchMode == LaunchMode.McpOnly ? McpOnlyArgument : DesktopArgument
        };
        if (HasNoBrowserFlag(args))
        {
            sanitized.Add(NoBrowserArgument);
        }

        if (port is { } validatedPort)
        {
            if (validatedPort is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), validatedPort, "The restart port must be from 1 through 65535.");
            }

            sanitized.Add(PortArgument);
            sanitized.Add(validatedPort.ToString(CultureInfo.InvariantCulture));
        }

        return sanitized;
    }

    internal static bool TryGetPort(string[] args, out int? port, out string? error)
    {
        if (!TryGetOptionValue(args, PortArgument, out var present, out var value))
        {
            port = null;
            error = $"The {PortArgument} flag requires a value.";
            return false;
        }

        if (!present)
        {
            port = null;
            error = null;
            return true;
        }

        if (!int.TryParse(value, out var parsed) || parsed is < 1 or > 65535)
        {
            port = null;
            error = $"The {PortArgument} value must be an integer from 1 through 65535.";
            return false;
        }

        port = parsed;
        error = null;
        return true;
    }

    internal static bool TryGetSetupCommand(string[] args, out SetupCommand? command, out string? error) =>
        TryGetSetupCommand(args, Environment.GetEnvironmentVariable, static () => Console.In.ReadLine(), out command, out error);

    internal static bool TryGetSetupCommand(string[] args,
        Func<string, string?> environmentReader,
        Func<string?> stdinReader,
        out SetupCommand? command,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(environmentReader);
        ArgumentNullException.ThrowIfNull(stdinReader);

        if (!HasArgument(args, SetupArgument))
        {
            command = null;
            error = null;
            return false;
        }

        var emailParsed = TryGetOptionValue(args, AdminEmailArgument, out var emailPresent, out var email);
        if (!emailParsed)
        {
            command = null;
            error = $"The {AdminEmailArgument} flag requires a value.";
            return true;
        }

        var passwordParsed = TryGetOptionValue(args, AdminPasswordArgument, out var passwordPresent, out var password);
        var stdinRequested = HasArgument(args, AdminPasswordStdinArgument);
        if (passwordPresent && stdinRequested)
        {
            command = null;
            error = $"Use either {AdminPasswordArgument} or {AdminPasswordStdinArgument}, not both.";
            return true;
        }

        if (!passwordParsed)
        {
            command = null;
            error = $"The {AdminPasswordArgument} flag requires a value.";
            return true;
        }

        if (!emailPresent)
        {
            email = environmentReader(AdminEmailEnvironmentVariable);
        }

        var passwordFromEnvironment = !passwordPresent && !stdinRequested;
        password = stdinRequested ? stdinReader()?.Trim() : password ?? environmentReader(AdminPasswordEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            command = null;
            error = $"The {SetupArgument} command requires an admin email and password.";
            return true;
        }

        command = new SetupCommand(email.Trim(), password, passwordFromEnvironment);
        error = null;
        return true;
    }

    internal static bool TryGetMcpKeyScope(string[] args, out McpServerApiKeyScope? scope, out string? error)
    {
        if (!TryGetOptionValue(args, McpKeyArgument, out var present, out var value))
        {
            scope = null;
            error = $"The {McpKeyArgument} flag requires delegate or agentic.";
            return true;
        }

        if (!present)
        {
            scope = null;
            error = null;
            return false;
        }

        if (string.Equals(value, "delegate", StringComparison.OrdinalIgnoreCase))
        {
            scope = McpServerApiKeyScope.Delegate;
            error = null;
            return true;
        }

        if (string.Equals(value, "agentic", StringComparison.OrdinalIgnoreCase))
        {
            scope = McpServerApiKeyScope.Agentic;
            error = null;
            return true;
        }

        scope = null;
        error = $"The {McpKeyArgument} scope must be delegate or agentic.";
        return true;
    }

    internal static bool TryGetResetAdminPassword(string[] args, out string? newPassword)
    {
        _ = TryGetOptionValue(args, ResetAdminPasswordArgument, out var present, out newPassword);
        if (string.IsNullOrEmpty(newPassword))
        {
            newPassword = null;
        }

        return present;
    }

    internal static KnowledgeDowngradeCommand GetKnowledgeDowngradeCommand(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var preflight = HasArgument(args, KnowledgeDowngradePreflightArgument);
        var export = HasArgument(args, KnowledgeDowngradeExportArgument);
        if (preflight && export)
        {
            throw new ArgumentException($"Use either {KnowledgeDowngradePreflightArgument} or {KnowledgeDowngradeExportArgument}, not both.", nameof(args));
        }

        if (export)
        {
            return KnowledgeDowngradeCommand.Export;
        }

        return preflight ? KnowledgeDowngradeCommand.Preflight : KnowledgeDowngradeCommand.None;
    }

    private static bool HasArgument(string[] args, string expected)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(argument => string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetOptionValue(string[] args, string option, out bool present, out string? value)
    {
        ArgumentNullException.ThrowIfNull(args);
        value = null;
        present = false;
        var prefix = option + "=";
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                present = true;
                value = argument[prefix.Length..];
                return value.Length > 0;
            }

            if (string.Equals(argument, option, StringComparison.OrdinalIgnoreCase))
            {
                present = true;
                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[index + 1];
                    return true;
                }

                return false;
            }
        }

        return true;
    }
}

internal enum LaunchMode
{
    Headless,
    Desktop,
    McpOnly
}

internal static class LaunchModeExtensions
{
    internal static bool IsLocalMode(this LaunchMode mode) =>
        mode is LaunchMode.Desktop or LaunchMode.McpOnly;
}

internal sealed record SetupCommand(string Email, string Password, bool PasswordFromEnvironment);

internal enum KnowledgeDowngradeCommand
{
    None,
    Preflight,
    Export
}
