namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     The structured outcome of a profile's test command, as read back by a code-owned result adapter.
///     <para>
///         Exit code alone cannot answer the question the validation gate actually asks. A test command that ran
///         nothing exits 0 on some runners, and a suite that was silently reduced to zero tests looks identical to a
///         suite that passed. So the gate needs counts, and it needs to know when it could not get them:
///         <see cref="Parsed" /> false means the adapter could not read a result, which fails validation. It never
///         means "assume it passed".
///     </para>
///     <para>
///         There is deliberately no portable assertion counter here. No such contract exists across TUnit, Vitest,
///         pytest and Cargo, and inventing one would be a fiction. What is portable is the four-number shape below;
///         each profile family's adapter is responsible for producing it from whatever its runner actually emits.
///     </para>
/// </summary>
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

/// <summary>
///     Reads a structured test result out of one profile command's raw output.
///     <para>
///         <strong>Adapters are code-owned per profile family and a profile may not supply one.</strong> That is a
///         reward-hacking control, not an architectural preference: a user-supplied success classifier is a
///         user-supplied definition of "green", and the whole point of the deterministic gate is that the definition
///         is the engine's. <see cref="DevelopmentTestResultAdapters.Resolve" /> therefore maps a profile id to an
///         adapter through code alone — there is no configuration seam to add one, and a custom profile resolves to
///         no adapter rather than to a caller-defined one.
///     </para>
/// </summary>
internal interface IDevelopmentTestResultAdapter
{
    /// <summary>The adapter's stable name, recorded on the outcome.</summary>
    string Name { get; }

    /// <summary>Whether this adapter reads results for the given command of the given profile.</summary>
    bool Handles(DevelopmentCommandProfile profile, string commandId);

    /// <summary>
    ///     Reads counts from the command's <em>untruncated</em> output. The caller must pass raw output: the summary a
    ///     runner emits is at the end, and the evidence copy is head-truncated to
    ///     <see cref="DevelopmentOptions.MaxCommandOutputBytes" />, so parsing the persisted copy would silently lose
    ///     the very lines this reads on any verbose repository.
    /// </summary>
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

        // A custom profile never resolves an adapter. It cannot run today either — DevelopmentCommandProfileCatalog
        // .ResolveStored rejects IsCustom outright — but stating it here keeps the control where the decision lives
        // rather than depending on a rejection in another class continuing to exist.
        return profile.IsCustom
            ? null
            : Array.Find(All, adapter => adapter.Handles(profile, commandId));
    }
}

/// <summary>
///     Reads <c>dotnet test</c> results for the code-owned <c>dotnet-slnx</c> and <c>dotnet-csproj</c> profiles.
///     <para>
///         It parses the Microsoft.Testing.Platform run summary rather than a TRX file. TRX is not available: under
///         MTP a <c>.trx</c> report requires the <em>target repository</em> to reference
///         <c>Microsoft.Testing.Extensions.TrxReport</c>, which a foreign repository cannot be required to do, and
///         adding a reporter argument would change the profile's canonical argv and therefore its digest. The summary
///         block is emitted by the platform itself for every MTP run.
///     </para>
///     <para>
///         Measured against the SDK's MTP runner on 2026-07-29, the summary is emitted on <strong>stdout</strong>
///         while the per-test lines go to <strong>stderr</strong>, and <c>No test projects were found.</c> is
///         stderr-only. Both streams are therefore searched. The shape parsed is:
///     </para>
///     <code>
///     Test run summary: Failed!
///       &lt;per-module lines&gt;
///
///       total: 5
///       failed: 1
///       succeeded: 3
///       skipped: 1
///       duration: 705ms
///     </code>
/// </summary>
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

        // Fail closed on a shape the adapter does not fully understand. A runner that grows a fifth bucket — a timed
        // out or cancelled test — would otherwise be silently dropped from `executed`, and a test that did not finish
        // must never be counted as one that passed.
        if (succeeded + failed + skipped != total)
        {
            return DevelopmentTestOutcome.ParseFailure(Name,
                DevelopmentTestParseFailureCodes.SummaryInconsistent,
                $"The test run summary does not add up: {succeeded} succeeded + {failed} failed + {skipped} skipped is not the reported total of {total}.");
        }

        return DevelopmentTestOutcome.Counts(Name, total, succeeded + failed, succeeded, failed);
    }

    /// <summary>
    ///     Reads the counts that follow the last summary marker. The last one is taken because the marker is a
    ///     per-run banner: a multi-module run emits one aggregate block, but taking the last is correct either way,
    ///     whereas taking the first would read one module's numbers as the whole run's.
    /// </summary>
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
