namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Text.RegularExpressions;

/// <summary>
///     Recognizes a containment wrapper (<c>setsid</c>, <c>systemd-run</c>, <c>unshare</c>, <c>env</c>, <c>bwrap</c>) that could not exec
///     the command it wraps.
/// </summary>
/// <remarks>
///     Behind a wrapper the engine starts the wrapper, which exists, so a missing executable is not a start failure but the wrapper's own
///     stderr line and exit code. Rewritten to <see cref="SandboxCommandResult.LaunchFailurePrefix" />, it reads exactly as an unwrapped
///     launch failure does, so every caller keys one rule. The line must name the requested executable and be the first stderr line of a
///     non-zero exit with no stdout; a workload that forges it can only report itself as not launched.
/// </remarks>
internal static partial class SandboxWrapperExecFailure
{
    /// <summary>The failed-launch <see cref="SandboxCommandResult.StandardError" />, or <see langword="null" /> when the command ran.</summary>
    public static string? Describe(string executable, int exitCode, string standardOutput, string standardError)
    {
        if (exitCode == 0 || !string.IsNullOrWhiteSpace(standardOutput))
        {
            return null;
        }

        var firstLine = standardError.AsSpan().TrimStart();
        var end = firstLine.IndexOfAny('\r', '\n');
        var line = (end < 0 ? firstLine : firstLine[..end]).ToString();
        var match = WrapperExecFailureLine().Match(line);

        return match.Success && string.Equals(match.Groups["executable"].Value, executable, StringComparison.Ordinal)
            ? $"{SandboxCommandResult.LaunchFailurePrefix} ({line})."
            : null;
    }

    // bwrap: "bwrap: execvp X: …"; util-linux: "setsid: failed to execute X: …", "unshare: failed to execute X: …"; coreutils and uutils:
    // "env: 'X': …"; systemd-run: "Failed to find executable X: …".
    [GeneratedRegex(
        @"^(?:bwrap: execvp |setsid: failed to execute |unshare: failed to execute |env: '|Failed to find executable |Failed to execute )(?<executable>.+?)'?: (?:No such file or directory|Permission denied)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex WrapperExecFailureLine();
}
