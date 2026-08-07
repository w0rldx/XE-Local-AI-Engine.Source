namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.RegularExpressions;

/// <summary>
///     Content-validation helpers shared by the executors (P2, execution-time defense in depth) and the CRUD service
///     (P3, author-time rejection). Kept in one place so the two layers can never disagree on what a legal custom tool
///     is: the same interpreter denylist, MAF-safe name rule, and absolute-path check gate both authoring and execution.
/// </summary>
internal static partial class CustomToolValidation
{
    /// <summary>The reserved MAF/OpenAI tool-name prefix every custom tool carries (<c>custom__{slug}</c>).</summary>
    public const string ToolNamePrefix = "custom__";

    // Shell/interpreter basenames (case-insensitive, ".exe" stripped) that must never be a command tool's executable:
    // running one turns the fixed-executable + single-argv guarantees into "run an arbitrary script", reopening shell
    // arg-injection (and the .NET BatBadBut cmd.exe hole). "python" is matched by prefix (python3, python3.12, …).
    private static readonly HashSet<string> InterpreterBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh", "bash", "dash", "zsh", "csh", "ksh", "fish",
        "cmd", "powershell", "pwsh",
        "node", "perl", "ruby", "env", "sudo", "ssh", "xargs", "awk"
    };

    // Script extensions the OS may execute through an interpreter (cmd.exe / Windows Script Host / PowerShell), so a
    // path ending in one is a shell surface regardless of its basename.
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".ps1", ".vbs"
    };

    [GeneratedRegex(@"^custom__[a-z0-9](?:[a-z0-9_]{0,48}[a-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolNameRegex();

    /// <summary>
    ///     True when <paramref name="name" /> is a MAF-safe custom tool name: the <c>custom__</c> prefix followed by a
    ///     lowercase <c>[a-z0-9_]</c> slug that starts and ends alphanumeric. Bounds the whole surface the model routes
    ///     against so a name can never carry a character that breaks the function-calling grammar.
    /// </summary>
    public static bool IsValidToolName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && ToolNameRegex().IsMatch(name);
    }

    /// <summary>True when <paramref name="path" /> is a rooted, fully-qualified absolute path (no PATH/CWD lookup).</summary>
    public static bool IsAbsolutePath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);
    }

    /// <summary>
    ///     True when <paramref name="executablePath" />'s basename is a known shell/interpreter or its extension is a
    ///     script extension — either of which must be rejected as a command tool's executable (C1/M4).
    /// </summary>
    public static bool IsInterpreterOrShell(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        // Split on BOTH separators regardless of host OS: Path.GetFileName does not treat '\' as a separator on Linux,
        // so a Windows-style path would otherwise slip its basename past the denylist. The executable is host-authored,
        // but the denylist is a trust-boundary check, so it must not depend on which OS parses the string.
        var trimmed = executablePath.Trim();
        var lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        var fileName = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        if (ScriptExtensions.Contains(extension))
        {
            return true;
        }

        // Compare on the name without a trailing ".exe" (Windows) so "python.exe" matches "python".
        var basename = string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^extension.Length]
            : fileName;

        return InterpreterBasenames.Contains(basename)
               || basename.StartsWith("python", StringComparison.OrdinalIgnoreCase);
    }
}
