namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.RegularExpressions;

/// <summary>
///     Content-validation helpers shared by the executors (execution-time defence in depth) and the CRUD service (author-time rejection).
/// </summary>
/// <remarks>
///     Kept in one place so the two layers can never disagree on what a legal custom tool is: the same interpreter denylist, MAF-safe name
///     rule and absolute-path check gate both authoring and execution.
/// </remarks>
internal static partial class CustomToolValidation
{
    /// <summary>The reserved MAF/OpenAI tool-name prefix every custom tool carries (<c>custom__{slug}</c>).</summary>
    public const string ToolNamePrefix = "custom__";

    // Shell/interpreter/exec-capable basenames (case-insensitive, ".exe" stripped) rejected as a command tool's executable; "python" matches by prefix.
    // A best-effort blocklist, NOT a sandbox: it closes shell arg-injection and the .NET BatBadBut cmd.exe hole, but per-call approval is the control.
    private static readonly HashSet<string> InterpreterBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "sh",
        "bash",
        "dash",
        "zsh",
        "csh",
        "ksh",
        "fish",
        "cmd",
        "powershell",
        "pwsh",
        "node",
        "perl",
        "ruby",
        "env",
        "sudo",
        "ssh",
        "xargs",
        "awk",
        "find",
        "busybox",
        "tar",
        "git",
        "make",
        "gdb",
        "nc",
        "ncat",
        "socat",
        "lua",
        "php",
        "deno",
        "bun",
        "rscript",
        "osascript",
        "tclsh"
    };

    // Script extensions the OS may execute through an interpreter (cmd.exe / Windows Script Host / PowerShell), so a
    // path ending in one is a shell surface regardless of its basename.
    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat",
        ".cmd",
        ".ps1",
        ".vbs"
    };

    [GeneratedRegex(@"^custom__[a-z0-9](?:[a-z0-9_]{0,48}[a-z0-9])?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolNameRegex();

    /// <summary>
    ///     True when <paramref name="name" /> is a MAF-safe custom tool name: the <c>custom__</c> prefix followed by a lowercase
    ///     <c>[a-z0-9_]</c> slug that starts and ends alphanumeric.
    /// </summary>
    /// <remarks>
    ///     Bounds the whole surface the model routes against, so a name can never carry a character that breaks the function-calling grammar.
    /// </remarks>
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
    ///     True when <paramref name="executablePath" />'s basename is a known shell, interpreter or exec-capable binary, or its extension is a
    ///     script extension — either of which must be rejected as a command tool's executable.
    /// </summary>
    /// <remarks>
    ///     A best-effort blocklist (see <see cref="InterpreterBasenames" />): a <see langword="false" /> result means the name is not on the
    ///     list, not that the executable is safe.
    /// </remarks>
    public static bool IsInterpreterOrShell(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        // Split on BOTH separators regardless of host OS: Path.GetFileName does not treat '\' as a separator on Linux, so a Windows-style path would
        // otherwise slip its basename past the denylist. The denylist is a trust-boundary check, so it must not depend on which OS parses the string.
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
