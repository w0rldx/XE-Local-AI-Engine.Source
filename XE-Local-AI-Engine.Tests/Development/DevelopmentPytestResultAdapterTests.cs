namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Covers the <c>python-pytest</c> result adapter and the verdict it feeds, over measured pytest output.</summary>
/// <remarks>
///     Captured on 2026-10-26 from pytest 9.1.1 running the profile's exact <c>-m pytest -q -p no:cacheprovider</c>
///     over throwaway suites, host paths shortened; the missing-module line is a host <c>python3</c> without pytest.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class DevelopmentPytestResultAdapterTests
{
    /// <summary>Exit 0: two passing tests and one skipped.</summary>
    private const string PassingOutput = """
                                         ..s                                                                      [100%]
                                         2 passed, 1 skipped in 0.00s
                                         """;

    /// <summary>Exit 1: one failing test.</summary>
    private const string FailingOutput = """
                                         .F                                                                       [100%]
                                         =================================== FAILURES ===================================
                                         ____________________________________ test_b ____________________________________

                                             def test_b():
                                         >       assert 1 == 2
                                         E       assert 1 == 2

                                         test_f.py:5: AssertionError
                                         =========================== short test summary info ============================
                                         FAILED test_f.py::test_b - assert 1 == 2
                                         1 failed, 1 passed in 0.01s
                                         """;

    /// <summary>Exit 5: a repository with no test at all.</summary>
    private const string NoTestsOutput = """

                                         no tests ran in 0.00s
                                         """;

    /// <summary>Exit 2: a test module that does not import. Its ImportError names a module, but never pytest itself.</summary>
    private const string CollectionErrorOutput = """
                                                 ==================================== ERRORS ====================================
                                                 __________________________ ERROR collecting test_c.py __________________________
                                                 test_c.py:1: in <module>
                                                     import nonexistent_mod
                                                 E   ModuleNotFoundError: No module named 'nonexistent_mod'
                                                 =========================== short test summary info ============================
                                                 ERROR test_c.py
                                                 !!!!!!!!!!!!!!!!!!!! Interrupted: 1 error during collection !!!!!!!!!!!!!!!!!!!!
                                                 1 error in 0.04s
                                                 """;

    /// <summary>Exit 1: every outcome bucket the adapter reads at once.</summary>
    private const string MixedOutput = """
                                       .xXE.                                                                    [100%]
                                       =========================== short test summary info ============================
                                       ERROR test_m.py::test_e - RuntimeError
                                       2 passed, 1 xfailed, 1 xpassed, 1 warning, 1 error in 0.01s
                                       """;

    /// <summary>Exit 1, stderr: the profile's bootstrap on a host interpreter that has no pytest module.</summary>
    private const string MissingPytestError = """
                                              Traceback (most recent call last):
                                                File "<string>", line 1, in <module>
                                                  import os, site, sys, pytest; root = os.getcwd(); sp = set(site.getsitepackages()); i = next((n for n, p in enumerate(sys.path) if p in sp), len(sys.path)); sys.path[i:i] = [root, os.path.join(root, 'src')]; os.environ['PYTEST_DISABLE_PLUGIN_AUTOLOAD'] = '1'; sys.argv[1:1] = ['-c', os.devnull]; raise SystemExit(pytest.main())
                                                  ^^^^^^^^^^^^^^^^^^^^^^^^^^^^
                                              ModuleNotFoundError: No module named 'pytest'

                                              """;

    private static readonly DevelopmentCommandProfile PytestProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.PythonPytest, buildTarget: null);

    [Test]
    public void Adapter_ResolvesForThePytestCommandOfThePytestProfileOnly()
    {
        AssertEx.Equal("pytest", AssertEx.NotNull(DevelopmentTestResultAdapters.Resolve(PytestProfile, DevelopmentCommandIds.PythonPytest)).Name);
        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(PytestProfile, DevelopmentCommandIds.GitDiffCheck));
        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(PytestProfile with
        {
            IsCustom = true
        }, DevelopmentCommandIds.PythonPytest));
        AssertEx.Null(DevelopmentTestResultAdapters.Resolve(DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "Fixture.slnx"),
            DevelopmentCommandIds.PythonPytest));
    }

    [Test]
    public void Parse_ReadsPassingAndFailingSummariesWithSkipsOutsideExecuted()
    {
        AssertCounts(Parse(PassingOutput), discovered: 3, executed: 2, passed: 2, failed: 0);
        AssertCounts(Parse(FailingOutput), discovered: 2, executed: 2, passed: 1, failed: 1);
    }

    /// <summary>Errors are failures and xpassed is a pass; xfailed ran nothing that could pass, so it counts as a skip.</summary>
    [Test]
    public void Parse_CountsErrorsAsFailuresAndIgnoresWarnings()
    {
        AssertCounts(Parse(MixedOutput), discovered: 5, executed: 4, passed: 3, failed: 1);
        AssertCounts(Parse(CollectionErrorOutput), discovered: 1, executed: 1, passed: 0, failed: 1);
    }

    /// <summary>A real zero, not a parse failure, so the verdict's "executed nothing" rule is what refuses it.</summary>
    [Test]
    public void Parse_ReportsAnEmptySuiteAsZeroCountsAndTheVerdictRefusesIt()
    {
        var outcome = Parse(NoTestsOutput);
        AssertCounts(outcome, discovered: 0, executed: 0, passed: 0, failed: 0);

        var verdict = Verdict(Evidence(exitCode: 5, outcome: outcome));
        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.NoTestsExecuted, verdict.FailureCode);
    }

    [Test]
    public void Parse_FailsClosedOnMissingTruncatedOrUnknownSummaries()
    {
        AssertEx.Equal(DevelopmentTestParseFailureCodes.SummaryNotFound, Parse("..\n").ParseFailureCode);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.OutputTruncated, Parse(PassingOutput, outputTruncated: true).ParseFailureCode);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.SummaryInconsistent, Parse("2 passed, 1 vanished in 0.01s").ParseFailureCode);
    }

    /// <summary>
    ///     A host without pytest exits 1 with nothing on stdout, which a lenient reader would call "no summary". It is
    ///     reported as what it is, and the verdict hands the operator the install instruction rather than a parse code.
    /// </summary>
    [Test]
    public void MissingPytest_FailsValidationWithTheInstallInstruction()
    {
        var outcome = Parse(string.Empty, MissingPytestError);
        AssertEx.False(outcome.Parsed);
        AssertEx.Equal(DevelopmentTestParseFailureCodes.RunnerNotInstalled, outcome.ParseFailureCode);

        var verdict = Verdict(Evidence(exitCode: 1, outcome: outcome));
        AssertEx.False(verdict.Passed);
        AssertEx.Contains(verdict.FailureDetail, "pytest is not installed");
        AssertEx.Contains(verdict.FailureDetail, "Install pytest on the host yourself");

        // A test's own ImportError is a collection failure of the repository, not a missing runner.
        AssertEx.NotEqual(DevelopmentTestParseFailureCodes.RunnerNotInstalled,
            Parse(CollectionErrorOutput, "E   ModuleNotFoundError: No module named 'pytest'\n").ParseFailureCode);
    }

    /// <summary>
    ///     No <c>python3</c> on the host is a command that never started, so it is also one that did not finish. The
    ///     install instruction must win over "did not finish", which would tell the operator nothing to act on.
    /// </summary>
    [Test]
    public void MissingPython_FailsValidationWithTheInstallInstructionRatherThanDidNotFinish()
    {
        var outcome = Parse(string.Empty, SandboxCommandResult.LaunchFailurePrefix + ".");
        AssertEx.Equal(DevelopmentTestParseFailureCodes.RunnerNotInstalled, outcome.ParseFailureCode);

        var verdict = Verdict(Evidence(exitCode: -1, outcome: outcome, completed: false));
        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.TestResultsUnparsed, verdict.FailureCode);
        AssertEx.Contains(verdict.FailureDetail, "python3 could not be started");
    }

    private static DevelopmentTestOutcome Parse(string standardOutput, string standardError = "", bool outputTruncated = false) =>
        new PytestTestResultAdapter().Parse(standardOutput, standardError, outputTruncated);

    private static void AssertCounts(DevelopmentTestOutcome outcome, int discovered, int executed, int passed, int failed)
    {
        AssertEx.True(outcome.Parsed, outcome.ParseFailureDetail);
        AssertEx.Equal(discovered, outcome.Discovered);
        AssertEx.Equal(executed, outcome.Executed);
        AssertEx.Equal(passed, outcome.Passed);
        AssertEx.Equal(failed, outcome.Failed);
    }

    private static DevelopmentValidationVerdict Verdict(DevelopmentCommandEvidence pytest) =>
        DevelopmentValidationVerdict.Evaluate(PytestProfile,
        [
            new DevelopmentCommandEvidence(DevelopmentCommandIds.GitDiffCheck, 0, Completed: true, OutputTruncated: false, 1, string.Empty, string.Empty),
            pytest
        ]);

    private static DevelopmentCommandEvidence Evidence(int exitCode, DevelopmentTestOutcome outcome, bool completed = true) =>
        new(DevelopmentCommandIds.PythonPytest,
            exitCode,
            completed,
            OutputTruncated: false,
            DurationMilliseconds: 1,
            StandardOutput: string.Empty,
            StandardError: string.Empty,
            outcome);
}
