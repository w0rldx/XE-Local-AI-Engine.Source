namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the code-owned test-result adapters and the verdict they feed.
///     <para>
///         The output strings below are <strong>measured</strong>, not invented: each was captured on 2026-07-29 by
///         running <c>dotnet test &lt;solution&gt; --configuration Release --no-build --max-parallel-test-modules 1</c>
///         — the exact argument vector <see cref="DevelopmentCommandProfileCatalog" /> materializes — against a
///         throwaway TUnit solution on the SDK's Microsoft.Testing.Platform runner. A parser pinned to a guessed
///         format is a parser that fails the first time it meets a real repository, so the fixtures are transcripts.
///     </para>
///     <para>
///         Two of those measurements are load-bearing and would not be guessed correctly: the run summary is written
///         to <strong>stdout</strong> while the per-test lines go to <strong>stderr</strong>, and
///         <c>No test projects were found.</c> is written to stderr alone with no summary block at all.
///     </para>
/// </summary>
public sealed class DevelopmentTestResultAdapterTests
{
    /// <summary>Measured stdout of a passing run: three tests, one of them skipped.</summary>
    private const string PassingSummary = """
                                          Test run summary: Passed!
                                            total: 3
                                            failed: 0
                                            succeeded: 2
                                            skipped: 1
                                            duration: 358ms
                                          """;

    /// <summary>Measured stdout of a run with one failing test, across two test modules.</summary>
    private const string FailingSummary = """
                                          Test run summary: Failed!
                                            /tmp/probe/tests/Second/bin/Release/net10.0/Second.dll (net10.0|x64) failed with 1 error(s) (223ms)
                                            /tmp/probe/tests/Probe/bin/Release/net10.0/Probe.dll (net10.0|x64) passed (232ms)

                                            total: 5
                                            failed: 1
                                            succeeded: 3
                                            skipped: 1
                                            duration: 705ms
                                          Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exitcodes)
                                          """;

    /// <summary>Measured stdout of a test module that contains no test at all — exit code 8.</summary>
    private const string ZeroTestsSummary = """
                                            Test run summary: Zero tests ran
                                              error: 1

                                              total: 0
                                              failed: 0
                                              succeeded: 0
                                              skipped: 0
                                              duration: 236ms
                                            Test run completed with non-success exit code: 8 (see: https://aka.ms/testingplatform/exitcodes)
                                            """;

    /// <summary>
    ///     Measured stdout of <c>dotnet test --no-build</c> run after the build failed: there is no test binary to
    ///     launch, and the platform still emits a fully readable all-zero summary (exit 1). Kept as its own fixture
    ///     because it is the case that most cleanly justifies the <c>executed &gt; 0</c> rule — nothing whatsoever
    ///     ran, and the output is not malformed in any way a parser could object to.
    /// </summary>
    private const string ZeroTestsAfterFailedBuildSummary = """
                                                            Test run summary: Zero tests ran
                                                              total: 0
                                                              failed: 0
                                                              succeeded: 0
                                                              skipped: 0
                                                              duration: 4ms
                                                            Test run completed with non-success exit code: 1 (see: https://aka.ms/testingplatform/exitcodes)
                                                            """;

    private static readonly DevelopmentCommandProfile SlnxProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "Fixture.slnx");

    [Test]
    public void Adapter_ResolvesForTheTestCommandOfBothDotnetProfilesAndForNothingElse()
    {
        var csproj = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetCsproj, "src/Lib/Lib.csproj");
        var generic = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        AssertEx.NotNull(DevelopmentTestResultAdapters.Resolve(SlnxProfile, DevelopmentCommandIds.DotnetTestRelease));
        AssertEx.NotNull(DevelopmentTestResultAdapters.Resolve(csproj, DevelopmentCommandIds.DotnetTestRelease));

        // Every non-test command produces no test result, so it must resolve nothing rather than an adapter that
        // then reports "could not parse" and fails a perfectly good build or whitespace check.
        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(SlnxProfile, DevelopmentCommandIds.DotnetBuildRelease));
        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(SlnxProfile, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(SlnxProfile, DevelopmentCommandIds.DotnetRestore));

        // generic-git runs no test command at all, so it has no adapter either.
        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(generic, DevelopmentCommandIds.DotnetTestRelease));
    }

    /// <summary>
    ///     Asserted structurally rather than by reading the code: a profile carrying <c>IsCustom</c> resolves no
    ///     adapter, so there is no path by which a repository-supplied profile could bring its own definition of what
    ///     counts as a passing test run. That is a reward-hacking control — a user-supplied success classifier is a
    ///     user-supplied definition of "green" — and it has to hold even if a custom profile ever becomes runnable.
    /// </summary>
    [Test]
    public void Adapter_NeverResolvesForACustomProfile()
    {
        var custom = SlnxProfile with
        {
            IsCustom = true
        };

        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(custom, DevelopmentCommandIds.DotnetTestRelease));
    }

    [Test]
    public void Parse_ReadsThePassingSummaryAndExcludesSkippedTestsFromExecuted()
    {
        var outcome = Parse(PassingSummary);

        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 3, outcome.Discovered);
        AssertEx.Equal(expected: 2, outcome.Executed);
        AssertEx.Equal(expected: 2, outcome.Passed);
        AssertEx.Equal(expected: 0, outcome.Failed);
    }

    /// <summary>
    ///     The multi-module case. The platform emits ONE aggregate block after the per-module lines, so the counts
    ///     must be the whole run's — reading a single module's numbers as the run's would under-report every failure
    ///     in a solution with more than one test project, which is every real repository.
    /// </summary>
    [Test]
    public void Parse_ReadsTheAggregateCountsAcrossTestModules()
    {
        var outcome = Parse(FailingSummary);

        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 5, outcome.Discovered);
        AssertEx.Equal(expected: 4, outcome.Executed);
        AssertEx.Equal(expected: 3, outcome.Passed);
        AssertEx.Equal(expected: 1, outcome.Failed);
    }

    /// <summary>
    ///     The case the whole slice exists for: a suite that ran nothing. It parses cleanly — the counts are real and
    ///     they are all zero — so the adapter reports it as parsed, and it is the verdict's <c>executed &gt; 0</c>
    ///     rule that rejects it. Getting this wrong in either direction is bad: calling it a parse failure would hide
    ///     an honest measurement, and calling it a pass is exactly the false green the gate must not produce.
    /// </summary>
    [Test]
    public void Parse_ReportsAZeroTestRunAsRealZeroCountsRatherThanAParseFailure()
    {
        var outcome = Parse(ZeroTestsSummary);

        AssertEx.True(outcome.Parsed);
        AssertEx.Equal(expected: 0, outcome.Discovered);
        AssertEx.Equal(expected: 0, outcome.Executed);

        // The same shape arrives by a completely different route — a broken build leaves no test binary to launch —
        // and it must read identically. Both are "nothing ran", and neither is a parse problem.
        var afterFailedBuild = Parse(ZeroTestsAfterFailedBuildSummary);
        AssertEx.True(afterFailedBuild.Parsed);
        AssertEx.Equal(expected: 0, afterFailedBuild.Executed);
    }

    /// <summary>
    ///     Measured: this message is written to stderr with no summary block anywhere, and it is the shape a
    ///     registered repository with no tests produces. It gets its own code because "this repository has no
    ///     tests" is an actionable state an operator can fix, while "the output could not be parsed" is not.
    /// </summary>
    [Test]
    public void Parse_DistinguishesARepositoryWithNoTestProjectFromUnreadableOutput()
    {
        var outcome = Parse(standardOutput: string.Empty, standardError: "No test projects were found.\n");

        AssertEx.False(outcome.Parsed);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.NoTestProjects, outcome.ParseFailureCode);
    }

    [Test]
    public void Parse_FailsWhenThereIsNoSummaryAtAll()
    {
        var outcome = Parse("MSBUILD : error MSB1009: Project file does not exist.\n");

        AssertEx.False(outcome.Parsed);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.SummaryNotFound, outcome.ParseFailureCode);
    }

    [Test]
    public void Parse_FailsWhenTheSummaryIsMissingACount()
    {
        var outcome = Parse("Test run summary: Passed!\n  total: 3\n  succeeded: 3\n");

        AssertEx.False(outcome.Parsed);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.SummaryIncomplete, outcome.ParseFailureCode);
    }

    /// <summary>
    ///     Fails closed on a shape the adapter does not fully account for. If the platform ever grows a fifth bucket
    ///     — a timed-out or cancelled test — those tests would silently vanish from <c>executed</c> and the run would
    ///     look greener than it was. Refusing to read a summary that does not add up is the conservative answer.
    /// </summary>
    [Test]
    public void Parse_FailsWhenTheCountsDoNotAddUp()
    {
        var outcome = Parse("Test run summary: Failed!\n  total: 5\n  failed: 1\n  succeeded: 1\n  skipped: 1\n");

        AssertEx.False(outcome.Parsed);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.SummaryInconsistent, outcome.ParseFailureCode);
    }

    /// <summary>
    ///     Truncated output cannot be trusted even when a summary is visible, because the summary is written last:
    ///     what survived truncation may be an earlier module's block rather than the run's.
    /// </summary>
    [Test]
    public void Parse_FailsWhenTheSandboxDroppedOutput()
    {
        var outcome = Parse(PassingSummary, standardError: string.Empty, outputTruncated: true);

        AssertEx.False(outcome.Parsed);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.OutputTruncated, outcome.ParseFailureCode);
    }

    [Test]
    public void Verdict_PassesOnlyWhenEveryCommandSucceededAndTestsActuallyRan()
    {
        var verdict = DevelopmentValidationVerdict.Evaluate(SlnxProfile,
        [
            Evidence(DevelopmentCommandIds.GitDiffCheck),
            Evidence(DevelopmentCommandIds.DotnetRestore),
            Evidence(DevelopmentCommandIds.DotnetBuildRelease),
            Evidence(DevelopmentCommandIds.DotnetTestRelease, outcome: DevelopmentTestOutcome.Counts("dotnet", discovered: 3, executed: 2, passed: 2, failed: 0))
        ]);

        AssertEx.True(verdict.Passed);
        AssertEx.Null(verdict.FailureCode);
    }

    /// <summary>
    ///     The reward-hacking case, stated as a rule rather than a hope: a test command that <em>exits zero</em> while
    ///     having executed nothing must not pass. The other gate checks — exit code, recorded command, and profile
    ///     digest — are all satisfied here.
    /// </summary>
    [Test]
    public void Verdict_RejectsATestCommandThatExitedZeroWithoutExecutingAnything()
    {
        var verdict = DevelopmentValidationVerdict.Evaluate(SlnxProfile,
        [
            Evidence(DevelopmentCommandIds.GitDiffCheck),
            Evidence(DevelopmentCommandIds.DotnetRestore),
            Evidence(DevelopmentCommandIds.DotnetBuildRelease),
            Evidence(DevelopmentCommandIds.DotnetTestRelease, outcome: DevelopmentTestOutcome.Counts("dotnet", discovered: 0, executed: 0, passed: 0, failed: 0))
        ]);

        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.NoTestsExecuted, verdict.FailureCode);
    }

    /// <summary>
    ///     A result the adapter could not read fails the run. It must never pass by default: "unreadable" is precisely
    ///     the state an agent optimizing for green would produce if unreadable meant "assume fine".
    /// </summary>
    [Test]
    public void Verdict_RejectsATestCommandWhoseResultCouldNotBeParsedEvenWhenItExitedZero()
    {
        var verdict = DevelopmentValidationVerdict.Evaluate(SlnxProfile,
        [
            Evidence(DevelopmentCommandIds.GitDiffCheck),
            Evidence(DevelopmentCommandIds.DotnetRestore),
            Evidence(DevelopmentCommandIds.DotnetBuildRelease),
            Evidence(DevelopmentCommandIds.DotnetTestRelease,
                outcome: DevelopmentTestOutcome.ParseFailure("dotnet", DevelopmentTestParseFailureCodes.SummaryNotFound, "no summary"))
        ]);

        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestResultsUnparsed, verdict.FailureCode);
    }

    /// <summary>
    ///     A failing suite is reported as failing tests, not as the generic "command failed" its exit code would also
    ///     support. The specific answer is the one the adapter just produced, and throwing it away to report an exit
    ///     code would make every red suite look like a broken toolchain.
    /// </summary>
    [Test]
    public void Verdict_ReportsFailingTestsRatherThanTheExitCodeTheyCaused()
    {
        var verdict = DevelopmentValidationVerdict.Evaluate(SlnxProfile,
        [
            Evidence(DevelopmentCommandIds.GitDiffCheck),
            Evidence(DevelopmentCommandIds.DotnetRestore),
            Evidence(DevelopmentCommandIds.DotnetBuildRelease),
            Evidence(DevelopmentCommandIds.DotnetTestRelease,
                exitCode: 2,
                outcome: DevelopmentTestOutcome.Counts("dotnet", discovered: 5, executed: 4, passed: 3, failed: 1))
        ]);

        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestsFailed, verdict.FailureCode);
    }

    /// <summary>
    ///     An earlier command's failure still wins, so a broken build is reported as a broken build rather than as the
    ///     unreadable test output it inevitably also produces.
    /// </summary>
    [Test]
    public void Verdict_ReportsTheFirstFailingCommandRatherThanItsDownstreamConsequence()
    {
        var verdict = DevelopmentValidationVerdict.Evaluate(SlnxProfile,
        [
            Evidence(DevelopmentCommandIds.GitDiffCheck),
            Evidence(DevelopmentCommandIds.DotnetRestore),
            Evidence(DevelopmentCommandIds.DotnetBuildRelease, exitCode: 1),
            Evidence(DevelopmentCommandIds.DotnetTestRelease,
                exitCode: 1,
                outcome: DevelopmentTestOutcome.ParseFailure("dotnet", DevelopmentTestParseFailureCodes.SummaryNotFound, "no summary"))
        ]);

        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.CommandFailed, verdict.FailureCode);
        AssertEx.Contains(verdict.FailureDetail, DevelopmentCommandIds.DotnetBuildRelease);
    }

    [Test]
    public void Verdict_RejectsEvidenceThatDoesNotCoverEveryDeclaredValidationCommand()
    {
        var verdict = DevelopmentValidationVerdict.Evaluate(SlnxProfile, [Evidence(DevelopmentCommandIds.GitDiffCheck)]);

        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.MissingCommandEvidence, verdict.FailureCode);
    }

    private static DevelopmentTestOutcome Parse(string standardOutput,
        string standardError = "",
        bool outputTruncated = false) =>
        new DotnetTestResultAdapter().Parse(standardOutput, standardError, outputTruncated);

    private static DevelopmentCommandEvidence Evidence(string commandId,
        int exitCode = 0,
        DevelopmentTestOutcome? outcome = null) =>
        new(commandId,
            exitCode,
            Completed: true,
            OutputTruncated: false,
            DurationMilliseconds: 1,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            outcome);
}
