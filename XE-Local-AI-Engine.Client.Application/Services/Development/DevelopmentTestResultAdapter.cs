namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>The structured outcome of a profile's test command, read back by a code-owned result adapter.</summary>
/// <remarks>
///     Exit code alone cannot answer what the gate asks: a command that ran nothing exits 0 on some runners, so a
///     suite silently reduced to zero tests looks like one that passed. A false <see cref="Parsed" /> means the
///     adapter could not read a result and validation fails; it never means "assume it passed". No portable
///     assertion counter exists across TUnit, Vitest, pytest and Cargo, so each adapter fills this four-number shape.
/// </remarks>
/// <param name="Adapter">The code-owned adapter that produced this, for evidence and for debugging a bad parse.</param>
/// <param name="Parsed">Whether counts were read. False means the counts are meaningless and validation fails.</param>
/// <param name="Discovered">Tests the runner knew about, including skipped ones.</param>
/// <param name="Executed">Tests that actually ran — <see cref="Passed" /> plus <see cref="Failed" />, excluding skips.</param>
/// <param name="Passed">Tests that ran and succeeded.</param>
/// <param name="Failed">Tests that ran and failed.</param>
/// <param name="ParseFailureCode">A stable code for why the parse failed, or null when it succeeded.</param>
/// <param name="ParseFailureDetail">Operator-facing detail for the parse failure, or null when it succeeded.</param>
internal sealed record DevelopmentTestOutcome(
    string Adapter,
    bool Parsed,
    int Discovered,
    int Executed,
    int Passed,
    int Failed,
    string? ParseFailureCode,
    string? ParseFailureDetail)
{
    public static DevelopmentTestOutcome Counts(string adapter, int discovered, int executed, int passed, int failed) =>
        new(adapter, true, discovered, executed, passed, failed, null, null);

    public static DevelopmentTestOutcome ParseFailure(string adapter, string code, string detail) =>
        new(adapter, false, 0, 0, 0, 0, code, detail);
}

/// <summary>Stable <see cref="DevelopmentTestOutcome.ParseFailureCode" /> values, so a UI can localize them.</summary>
internal static class DevelopmentTestParseFailureCodes
{
    /// <summary>The runner reported that the build target contains no test project at all.</summary>
    public const string NoTestProjects = "no_test_projects";

    /// <summary>No result summary was present in the command output.</summary>
    public const string SummaryNotFound = "summary_not_found";

    /// <summary>A summary was present but did not carry every count the adapter needs.</summary>
    public const string SummaryIncomplete = "summary_incomplete";

    /// <summary>The counts were present but did not agree with each other.</summary>
    public const string SummaryInconsistent = "summary_inconsistent";

    /// <summary>Output was truncated, so the summary may have been cut off and cannot be trusted.</summary>
    public const string OutputTruncated = "output_truncated";
}

/// <summary>Reads a structured test result out of one profile command's raw output.</summary>
/// <remarks>
///     Adapters are code-owned per profile family and a profile may not supply one — a reward-hacking control, not a
///     preference: a user-supplied success classifier is a user-supplied definition of "green", and the gate's whole
///     point is that the definition is the engine's. <see cref="DevelopmentTestResultAdapters.Resolve" /> maps a
///     profile id to an adapter through code alone, so a custom profile resolves to no adapter at all.
/// </remarks>
internal interface IDevelopmentTestResultAdapter
{
    /// <summary>The adapter's stable name, recorded on the outcome.</summary>
    string Name { get; }

    /// <summary>Whether this adapter reads results for the given command of the given profile.</summary>
    bool Handles(DevelopmentCommandProfile profile, string commandId);

    /// <summary>Reads counts from the command's <em>untruncated</em> output.</summary>
    /// <remarks>
    ///     The caller must pass raw output: a runner emits its summary at the end, and the evidence copy is
    ///     head-truncated to <see cref="DevelopmentOptions.MaxCommandOutputBytes" />, so parsing the persisted copy
    ///     would silently lose the very lines this reads on any verbose repository.
    /// </remarks>
    DevelopmentTestOutcome Parse(string standardOutput, string standardError, bool outputTruncated);
}

/// <summary>
///     The code-owned adapter registry. Resolution is by profile family and nothing else.
/// </summary>
internal static class DevelopmentTestResultAdapters
{
    private static readonly IDevelopmentTestResultAdapter[] All = [new DotnetTestResultAdapter()];

    /// <summary>
    ///     The adapter for this profile's command, or null when the command produces no test result to read (every
    ///     non-test command, and every profile family that has no adapter yet).
    /// </summary>
    public static IDevelopmentTestResultAdapter? Resolve(DevelopmentCommandProfile profile, string commandId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);

        // A custom profile never resolves an adapter. Stated here so the control lives with the decision rather than
        // depending on DevelopmentCommandProfileCatalog.ResolveStored continuing to reject IsCustom outright.
        return profile.IsCustom
            ? null
            : Array.Find(All, adapter => adapter.Handles(profile, commandId));
    }
}

/// <summary>
///     Reads <c>dotnet test</c> results for the code-owned <c>dotnet-slnx</c> and <c>dotnet-csproj</c> profiles.
/// </summary>
/// <remarks>
///     It parses the Microsoft.Testing.Platform run summary — the <c>total</c>, <c>failed</c>, <c>succeeded</c> and
///     <c>skipped</c> lines under a "Test run summary:" banner, which the platform emits for every MTP run — rather
///     than a TRX file, which needs the target repository to reference <c>Microsoft.Testing.Extensions.TrxReport</c>
///     and a reporter argument that would change the profile's canonical argv and digest. Both streams are searched:
///     the summary goes to stdout, the per-test lines to stderr, and "No test projects were found." is stderr-only.
/// </remarks>
internal sealed class DotnetTestResultAdapter : IDevelopmentTestResultAdapter
{
    private const string SummaryMarker = "Test run summary:";
    private const string NoTestProjectsMarker = "No test projects were found.";

    public string Name => "dotnet";

    public bool Handles(DevelopmentCommandProfile profile, string commandId)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Equals(commandId, DevelopmentCommandIds.DotnetTestRelease, StringComparison.Ordinal)
               && (string.Equals(profile.ProfileId, DevelopmentCommandProfileCatalog.DotnetSlnx, StringComparison.Ordinal)
                   || string.Equals(profile.ProfileId, DevelopmentCommandProfileCatalog.DotnetCsproj, StringComparison.Ordinal));
    }

    public DevelopmentTestOutcome Parse(string standardOutput, string standardError, bool outputTruncated)
    {
        var output = standardOutput ?? string.Empty;
        var error = standardError ?? string.Empty;

        // Checked before the truncation guard: "no test projects" is a complete, unambiguous verdict that appears at
        // the very start of the output, so it survives truncation and is far more useful than "we could not read it".
        if (output.Contains(NoTestProjectsMarker, StringComparison.Ordinal)
            || error.Contains(NoTestProjectsMarker, StringComparison.Ordinal))
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.NoTestProjects,
                "The profile declares a test command, but the build target contains no test project.");
        }

        if (outputTruncated)
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.OutputTruncated,
                "The test command produced more output than the evidence cap allows, so the run summary was cut off and no result could be read.");
        }

        var summary = ReadSummary(output) ?? ReadSummary(error);
        if (summary is null)
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.SummaryNotFound,
                "The test command produced no run summary, so no test result could be read.");
        }

        if (summary.Total is not { } total
            || summary.Failed is not { } failed
            || summary.Succeeded is not { } succeeded
            || summary.Skipped is not { } skipped)
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.SummaryIncomplete,
                "The test run summary did not carry every count the gate needs (total, failed, succeeded, skipped).");
        }

        // Fail closed on a shape the adapter does not fully understand: a fifth bucket, a timed-out or cancelled test,
        // would drop silently out of the executed count, and a test that did not finish is never one that passed.
        if (succeeded + failed + skipped != total)
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.SummaryInconsistent,
                $"The test run summary does not add up: {succeeded} succeeded + {failed} failed + {skipped} skipped is not the reported total of {total}.");
        }

        return DevelopmentTestOutcome.Counts(Name, total, succeeded + failed, succeeded, failed);
    }

    /// <summary>Reads the counts that follow the LAST summary marker.</summary>
    /// <remarks>
    ///     The marker is a per-run banner and a multi-module run emits one aggregate block, so taking the last is
    ///     correct either way, whereas taking the first would read one module's numbers as the whole run's.
    /// </remarks>
    private static SummaryCounts? ReadSummary(string text)
    {
        var markerIndex = text.LastIndexOf(SummaryMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var counts = new SummaryCounts();
        foreach (var rawLine in text[markerIndex..].Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!int.TryParse(line[(separator + 1)..].Trim(), out var value))
            {
                continue;
            }

            // First occurrence wins: the aggregate block is the first set of bare counts after the banner, and a
            // later per-module block must not overwrite it.
            switch (key)
            {
                case "total":
                    counts.Total ??= value;
                    break;
                case "failed":
                    counts.Failed ??= value;
                    break;
                case "succeeded":
                    counts.Succeeded ??= value;
                    break;
                case "skipped":
                    counts.Skipped ??= value;
                    break;
                default:
                    break;
            }
        }

        return counts;
    }

    private sealed class SummaryCounts
    {
        public int? Total { get; set; }
        public int? Failed { get; set; }
        public int? Succeeded { get; set; }
        public int? Skipped { get; set; }
    }
}
