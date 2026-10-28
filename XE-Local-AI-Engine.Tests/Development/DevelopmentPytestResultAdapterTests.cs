namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Development.Implementation;
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
        AssertEx.Equal(DevelopmentValidationFailureCodes.RunnerNotInstalled, verdict.FailureCode);
        AssertEx.True(verdict.StopsTask, "no coder round can install pytest, so the task stops for a human.");
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
        AssertEx.Equal(DevelopmentValidationFailureCodes.RunnerNotInstalled, verdict.FailureCode);
        AssertEx.True(verdict.StopsTask);
        AssertEx.Contains(verdict.FailureDetail, "python3 could not be started");
    }

    /// <summary>A boundary that could not be set up is not a missing Python, so the hint must not tell the operator to install one.</summary>
    [Test]
    public void ABoundaryFailure_StopsTheTaskWithoutTellingTheOperatorToInstallPython()
    {
        var outcome = Parse(string.Empty, SandboxCommandResult.BoundaryFailurePrefix + " ('/opt/py' is writable by group or world).");

        AssertEx.Equal(DevelopmentTestParseFailureCodes.RunnerNotInstalled, outcome.ParseFailureCode);
        AssertEx.Contains(outcome.ParseFailureDetail, "filesystem boundary could not be established");
        AssertEx.False(AssertEx.NotNull(outcome.ParseFailureDetail).Contains("Install Python", StringComparison.Ordinal), outcome.ParseFailureDetail);
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

    /// <summary>
    ///     A runner the boundary left out is unreachable, not missing: the detail names the tree and the rule and never says "not
    ///     installed"; a skip for another executable changes nothing.
    /// </summary>
    [Test]
    public void AMissingRunnerWhoseExecutableTheBoundaryLeftOut_IsReportedUnreachable()
    {
        var evidence = Evidence(exitCode: 1, Parse(string.Empty, MissingPytestError));
        DevelopmentSkippedToolchainTree[] skipped = [Skipped("python3", "/tmp/pytest-venv", "it lies under a mount point the sandbox owns (such as /tmp), where it would be shadowed")];

        var verdict = Verdict(evidence, skipped);

        AssertEx.Equal(DevelopmentValidationFailureCodes.RunnerNotInstalled, verdict.FailureCode);
        AssertEx.True(verdict.StopsTask);
        var detail = AssertEx.NotNull(verdict.FailureDetail);
        AssertEx.Contains(detail, "behind the filesystem boundary");
        AssertEx.Contains(detail, "'[REDACTED:development-path]' (it lies under a mount point");
        AssertEx.False(detail.Contains("is not installed", StringComparison.Ordinal), detail);

        AssertEx.Contains(Verdict(evidence, [Skipped("dotnet", "/opt/dotnet-shared", "it is writable by group or world")]).FailureDetail, "pytest is not installed");
    }

    /// <summary>
    ///     The unreachable-runner detail reaches the UI, the task's blocked reason and downstream workflow nodes, so its paths are redacted
    ///     as command output is: no account home, a layout-free tail kept. The warning log keeps the full path.
    /// </summary>
    [Test]
    public void TheUnreachableRunnerDetail_RedactsTheHomeAndKeepsATail()
    {
        const string tree = "/home/operator/.local/share/mise/installs/python/latest";
        var verdict = Verdict(Evidence(exitCode: 1, Parse(string.Empty, MissingPytestError)), [Skipped("python3", tree, $"'{tree}' is writable by group or world")]);
        var detail = AssertEx.NotNull(verdict.FailureDetail);

        AssertEx.Contains(detail, "'[REDACTED:development-path]/python/latest'");
        AssertEx.False(detail.Contains("operator", StringComparison.Ordinal), detail);
        AssertEx.False(AssertEx.NotNull(DevelopmentValidationRunner.ReasonFor(verdict)).Contains("operator", StringComparison.Ordinal), "the task's blocked reason carries the same detail.");
    }

    /// <summary>
    ///     A left-out tree at a long path with a UUID and hash-named segments (a session scratchpad under /tmp, a Nix store entry) still
    ///     yields a validation report the persistence scan accepts, carrying the unreachable detail.
    /// </summary>
    /// <remarks>
    ///     Two causes, both measured: the report JSON escapes <c>'</c> as <c>\u0027</c>, so a quoted path met the path redaction behind a
    ///     <c>7</c> and survived whole; and a redacted path keeps its last two segments, so a hash-named one (Nix) kept a
    ///     high-entropy run. Either way the scanner's entropy rule rejected the whole report.
    /// </remarks>
    [Test]
    [Arguments("/tmp/claude-1000/-home-operator-projects-XE-Local-AI-Engine-Source/88e527ca-8f96-4a86-8d41-0291c48cbe9a/scratchpad/tmp-venv")]
    [Arguments("/nix/store/k3vq9x2m7b1lzfj0c8hd5r4wp6sy2nta-python3-3.12.8-env")]
    public void AnUnreachableRunnerAtAHashNamedPath_StillYieldsAPersistableReport(string tree)
    {
        var verdict = Verdict(Evidence(exitCode: 1, Parse(string.Empty, MissingPytestError)),
            [Skipped("python3", tree, "it lies under a mount point the sandbox owns (such as /tmp, /etc or /xe-runtime), where it would be shadowed"),
                Skipped("python3", tree + "/bin/python3", "it is a link to '/home/operator/.local/share/mise/installs/python/3.14.8/bin/python3.14', which is not a directory inside a bound toolchain tree")]);
        var report = new DevelopmentValidationReport(false,
            new string('a', 40),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "development-validation-v2",
            "python-pytest",
            new string('e', 64),
            verdict.FailureCode,
            verdict.FailureDetail,
            [],
            1,
            FilesystemBoundary: true);

        var persisted = DevelopmentArtifactSanitizer.SanitizeText(System.Text.Encoding.UTF8.GetString(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, ReportJsonOptions)));

        AssertEx.Contains(persisted, "could not reach its runner behind the filesystem boundary");
        AssertEx.False(AssertEx.NotNull(verdict.FailureDetail).Contains(tree, StringComparison.Ordinal), "the tree is quoted in its redacted form.");
    }

    /// <summary>A report the secret scanner refused twice tells the operator so, rather than naming only an exception type.</summary>
    [Test]
    public void ARecoveryExhaustedByTheSecretScanner_SaysSo()
    {
        var refusal = AssertEx.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentArtifactSanitizer.SanitizeText("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----"));

        AssertEx.Contains(DevelopmentValidationRunner.BuildRecoveryExhaustedReason(refusal), "text the secret scanner refused");
        AssertEx.Contains(DevelopmentValidationRunner.BuildRecoveryExhaustedReason(new InvalidOperationException("boom")), "(InvalidOperationException)");
    }

    private static readonly System.Text.Json.JsonSerializerOptions ReportJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private static DevelopmentSkippedToolchainTree Skipped(string executable, string tree, string reason) =>
        new()
        {
            Executable = executable,
            Tree = tree,
            Reason = reason
        };

    private static DevelopmentValidationVerdict Verdict(DevelopmentCommandEvidence pytest, IReadOnlyList<DevelopmentSkippedToolchainTree>? unreachable = null) =>
        DevelopmentValidationVerdict.Evaluate(PytestProfile,
        [
            new DevelopmentCommandEvidence(DevelopmentCommandIds.GitDiffCheck, 0, Completed: true, OutputTruncated: false, 1, string.Empty, string.Empty),
            pytest
        ],
            unreachable);

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
