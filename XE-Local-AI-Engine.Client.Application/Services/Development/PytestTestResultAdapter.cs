namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Reads <c>pytest -q</c> results for the code-owned <c>python-pytest</c> profile.</summary>
/// <remarks>
///     It parses pytest's final summary line (<c>2 passed, 1 skipped in 0.01s</c>, or <c>no tests ran in 0.00s</c>).
///     Errors count as failures and xpassed as passed; an outcome word it does not know fails the parse. pytest is the
///     operator's host tool, so a host without it is reported as such and never as a readable zero.
/// </remarks>
internal sealed partial class PytestTestResultAdapter : IDevelopmentTestResultAdapter
{
    private const string MissingModuleLine = "ModuleNotFoundError: No module named 'pytest'";
    private const string NoTestsRan = "no tests ran";

    public string Name => "pytest";

    public bool Handles(DevelopmentCommandProfile profile, string commandId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Equals(commandId, DevelopmentCommandIds.PythonPytest, StringComparison.Ordinal)
               && string.Equals(profile.ProfileId, DevelopmentCommandProfileCatalog.PythonPytest, StringComparison.Ordinal);
    }

    public DevelopmentTestOutcome Parse(string standardOutput, string standardError, bool outputTruncated)
    {
        var output = standardOutput ?? string.Empty;
        var error = standardError ?? string.Empty;

        if (error.StartsWith(SandboxCommandResult.LaunchFailurePrefix, StringComparison.Ordinal))
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.RunnerNotInstalled,
                "python3 could not be started on this host. Install Python 3 and pytest on the host yourself; XE never installs either.");
        }

        // The bootstrap's own final traceback line; a test's collection error is indented under "E" and never matches.
        if (error.Split('\n').Any(static line => string.Equals(line.Trim(), MissingModuleLine, StringComparison.Ordinal)))
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.RunnerNotInstalled,
                "pytest is not installed for the host's python3. Install pytest on the host yourself; XE never installs it.");
        }

        if (outputTruncated)
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.OutputTruncated,
                "The test command produced more output than the evidence cap allows, so the pytest summary was cut off and no result could be read.");
        }

        var summary = LastSummary(output) ?? LastSummary(error);
        if (summary is null)
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.SummaryNotFound,
                "The test command produced no pytest summary line, so no test result could be read.");
        }

        if (string.Equals(summary, NoTestsRan, StringComparison.Ordinal))
        {
            return DevelopmentTestOutcome.Counts(Name, discovered: 0, executed: 0, passed: 0, failed: 0);
        }

        int passed = 0, failed = 0, skipped = 0;
        foreach (var part in summary.Split(", "))
        {
            var separator = part.IndexOf(' ', StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(part[..separator], out var count))
            {
                return Inconsistent(summary);
            }

            switch (part[(separator + 1)..])
            {
                case "passed" or "xpassed":
                    passed += count;
                    break;
                case "failed" or "error" or "errors":
                    failed += count;
                    break;
                case "skipped" or "xfailed":
                    skipped += count;
                    break;
                case "deselected" or "warning" or "warnings" or "rerun" or "reruns":
                    break;
                default:
                    return Inconsistent(summary);
            }
        }

        return DevelopmentTestOutcome.Counts(Name, passed + failed + skipped, passed + failed, passed, failed);
    }

    private DevelopmentTestOutcome Inconsistent(string summary) =>
        DevelopmentTestOutcome.ParseFailure(Name,
            DevelopmentTestParseFailureCodes.SummaryInconsistent,
            $"The pytest summary '{summary}' carries an outcome the adapter does not read, so no result could be trusted.");

    /// <summary>The counts part of the LAST summary line, or null when there is none.</summary>
    private static string? LastSummary(string text)
    {
        string? found = null;
        foreach (var line in text.Split('\n'))
        {
            var match = SummaryLine().Match(line.Trim());
            if (match.Success)
            {
                found = match.Groups["counts"].Value;
            }
        }

        return found;
    }

    [GeneratedRegex(@"^=*\s*(?<counts>(no tests ran|\d+ [a-z]+(, \d+ [a-z]+)*)) in \d+(\.\d+)?s(econds)?( \([0-9:]+\))?\s*=*$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        100)]
    private static partial Regex SummaryLine();
}
