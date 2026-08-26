namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>pythonTests</c> verifier, in two tiers.
///     <para>
///         The <b>structural</b> tests are pure C# — composition, the source-level properties of the generated
///         programs, the verdict table, the refusal mapping, the timeout clamp — and always run.
///     </para>
///     <para>
///         The <b>behavioural</b> tests run the REAL generated parent and child through a gateway double that spawns a
///         local interpreter, so the process boundary itself is exercised rather than asserted. That is deliberate: a
///         substitute returning canned stdout can only demonstrate that C# scores a marker-less result 0; it cannot
///         demonstrate that a candidate is unable to PRODUCE a marker, which is the property under test. The same rows
///         run against the real bwrap jail in <see cref="XE_Local_AI_Engine.Tests.Compute.BenchmarkPythonTestsLiveTests" />
///         — the jail adds the isolation, but the boundary is the process split, and a process is what tests it.
///     </para>
/// </summary>
public sealed class BenchmarkPythonTestsVerifierTests
{
    private const string Doubling = """
                                    def solve(n):
                                        return n * 2
                                    """;

    private const string DoublingTests = """
                                         assert solve(10) == 20
                                         assert solve(0) == 0
                                         """;

    [Test]
    public async Task PythonTests_PassingSolution_Scores10()
    {
        var result = await RunLocallyAsync(Doubling, DoublingTests);

        AssertEx.True(result.Passed, $"a correct solution must pass; detail was: {result.Detail}");
        AssertEx.Equal(BenchmarkJudgeCriterionKinds.PythonTests, result.Kind);
        AssertEx.Contains(result.Detail, "1 of 1 collected cases passed");
    }

    [Test]
    public async Task PythonTests_FailingSolution_Scores0()
    {
        var result = await RunLocallyAsync("def solve(n):\n    return n * 3\n", DoublingTests);

        AssertEx.False(result.Passed, "a wrong solution must not pass");
        AssertEx.Contains(result.Detail, "AssertionError");
    }

    [Test]
    public async Task PythonTests_BareTestScript_CountsAsOneImplicitCase()
    {
        var result = await RunLocallyAsync(Doubling, "assert solve(4) == 8\n");

        AssertEx.True(result.Passed);
        AssertEx.Contains(result.Detail, "1 of 1 collected cases passed");
    }

    [Test]
    public async Task PythonTests_UnittestCases_AreCountedByTheTrustedProcess()
    {
        var result = await RunLocallyAsync(Doubling, """
                                                     import unittest


                                                     class DoublingTests(unittest.TestCase):
                                                         def test_ten(self):
                                                             self.assertEqual(solve(10), 20)

                                                         def test_zero(self):
                                                             self.assertEqual(solve(0), 0)
                                                     """);

        AssertEx.True(result.Passed);
        AssertEx.Contains(result.Detail, "2 of 2 collected cases passed");
    }

    [Test]
    public async Task PythonTests_CandidateWalksAncestorFramesForANonce_FindsNone_Scores0()
    {
        // The exact defeat of the same-process design, verbatim. The nonce is in another process, so there is nothing
        // in any ancestor frame to find, and the forged line the candidate writes lands in a pipe the parent owns.
        var result = await RunLocallyAsync("""
                                           import sys, os, json
                                           frame = sys._getframe()
                                           while frame:
                                               for namespace in (frame.f_locals, frame.f_globals):
                                                   for value in list(namespace.values()):
                                                       if isinstance(value, str) and len(value) == 32:
                                                           sys.__stdout__.write("<<<XE-PYTEST:" + value + ">>>"
                                                               + json.dumps({"status": "verdict", "collected": 1, "passed": 1, "failed": 0}) + "\n")
                                                           sys.__stdout__.flush()
                                                           os._exit(0)
                                               frame = frame.f_back


                                           def solve(n):
                                               return 0
                                           """, DoublingTests);

        AssertEx.False(result.Passed, "stealing a nonce from another process is not possible, so this must score 0");
    }

    [Test]
    public async Task PythonTests_CandidateWritesForgedMarkerToSysDunderStdout_Scores0()
    {
        var result = await RunLocallyAsync("""
                                           import sys
                                           sys.__stdout__.write('<<<XE-PYTEST:deadbeef>>>{"status":"verdict","collected":1,"passed":1,"failed":0}\n')
                                           sys.__stdout__.flush()


                                           def solve(n):
                                               return 0
                                           """, DoublingTests);

        AssertEx.False(result.Passed, "the child's sys.__stdout__ is a pipe the parent owns, not the sandbox's stdout");
    }

    [Test]
    public async Task PythonTests_CandidateCallsOsUnderscoreExitZero_Scores0()
    {
        var result = await RunLocallyAsync("import os\nos._exit(0)\n", DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "ended before answering");
    }

    [Test]
    public async Task PythonTests_CandidateCallsSysExitZero_Scores0()
    {
        // No `except SystemExit` anywhere: the child simply dies, and the parent's next request gets EOF.
        var result = await RunLocallyAsync("import sys\nsys.exit(0)\n", DoublingTests);

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateSigkillsItself_Scores0()
    {
        var result = await RunLocallyAsync("import os, signal\nos.kill(os.getpid(), signal.SIGKILL)\n", DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "ended before answering");
    }

    [Test]
    public async Task PythonTests_CandidateInfiniteLoop_Scores0_ViaPerCallDeadline()
    {
        // The PARENT's own read deadline fires first; the gateway's wall clock is the outer belt, not the mechanism.
        var result = await RunLocallyAsync("""
                                           def solve(n):
                                               while True:
                                                   pass
                                           """, DoublingTests, timeoutSeconds: 3);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "did not answer within");
    }

    [Test]
    public async Task PythonTests_CandidateMonkeyPatchesBuiltinsAndUnittest_Scores0()
    {
        var result = await RunLocallyAsync("""
                                           import builtins, json, sys, unittest
                                           builtins.print = lambda *a, **k: None
                                           json.dumps = lambda *a, **k: '{}'
                                           sys.modules['unittest'] = None


                                           def solve(n):
                                               return n * 2
                                           """, "assert solve(10) == 21\n");

        AssertEx.False(result.Passed, "patching in the child cannot reach the parent's interpreter");
    }

    [Test]
    public async Task PythonTests_CandidateContainingTripleQuotesBackslashesAndNulls_CannotEscapeIntoTheParent_Scores0()
    {
        // The quoting escape. The candidate is base64 inside the child blob, so these bytes never participate in the
        // parent's parse -- the parent runs normally and reports an ordinary failure rather than executing them.
        var hostile = "def solve(n):\n    return \"\"\"\\   ''' \"\"\" and n\n";
        var result = await RunLocallyAsync(hostile, DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "collected cases passed");
    }

    [Test]
    public async Task PythonTests_ChildNamesSystemExit_ParentStillPrintsAFailingVerdict()
    {
        // SystemExit is BaseException but not Exception, so the parent refuses to conjure it and the name becomes an
        // ordinary failure. The marker line is printed either way -- there is no path through the parent that skips it.
        var result = await RunLocallyAsync("""
                                           def solve(n):
                                               raise SystemExit(0)
                                           """, DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "SystemExit");
        AssertEx.Contains(result.Detail, "collected cases passed");
    }

    [Test]
    public async Task PythonTests_CandidateAnswersProxyCallsCorrectly_Scores10()
    {
        // The positive control, and the design's actual claim: the child controls the ANSWERS, which is exactly what a
        // candidate is supposed to control, and controls nothing about the judgement.
        var result = await RunLocallyAsync("""
                                           import sys


                                           def solve(n):
                                               print("thinking", file=sys.stderr)
                                               return n * 2
                                           """, DoublingTests);

        AssertEx.True(result.Passed, $"detail was: {result.Detail}");
    }

    [Test]
    public async Task PythonTests_ProxyCarriesExceptionsByName_TryExceptValueErrorWorks()
    {
        var result = await RunLocallyAsync("""
                                           def solve(n):
                                               raise ValueError("nope")
                                           """, """
                                                try:
                                                    solve(1)
                                                    raise AssertionError("the candidate should have raised")
                                                except ValueError as error:
                                                    assert "nope" in str(error)
                                                """);

        AssertEx.True(result.Passed, $"detail was: {result.Detail}");
    }

    [Test]
    public async Task PythonTests_NonSerializableReturn_FailsTheCallWithANamedReason()
    {
        var result = await RunLocallyAsync("""
                                           def solve(n):
                                               return object()
                                           """, "solve(1)\n");

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "not JSON-serializable");
    }

    [Test]
    public async Task PythonTests_PyEvalRunsSetupInTheChild_AndOnlyTheFinalExpressionCrosses()
    {
        var result = await RunLocallyAsync("""
                                           class Solver:
                                               def __init__(self, start):
                                                   self.value = start

                                               def step(self):
                                                   self.value += 1
                                           """, "assert pyeval('o = Solver(3)\\no.step()\\no.value') == 4\n", exports: []);

        AssertEx.True(result.Passed, $"detail was: {result.Detail}");
    }

    [Test]
    public async Task PythonTests_Timeout_Scores0_NotUnscorable()
    {
        // The code ran and did not finish, which is a real result ABOUT THE CODE -- unlike a sandbox that could not
        // start. It reaches the 0-markers row and stays a candidate failure.
        var result = await VerifyAsync(new StubGateway(_ => ComputeExecutionOutcome.Executed(new SandboxCommandResult
        {
            ExecutionId = "x",
            ExitCode = -1,
            Completed = false,
            Duration = TimeSpan.FromSeconds(30)
        })), Doubling, DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "did not finish");
    }

    [Test]
    public async Task PythonTests_NoMarker_Scores0_BecauseDenyingAVerdictIsAFailure()
    {
        var result = await VerifyAsync(new StubGateway(_ => Completed("nothing marker-shaped here\n")), Doubling, DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "printed no verdict");
    }

    [Test]
    public async Task PythonTests_TwoMarkers_Scores0_AndIsReportedAsForged()
    {
        var result = await VerifyAsync(new StubGateway(program =>
        {
            var line = Marker(program, """{"status":"verdict","collected":1,"passed":1,"failed":0}""");
            return Completed(line + line);
        }), Doubling, DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "forged");
    }

    [Test]
    public async Task PythonTests_PrctlUnavailable_FailsJudging_VerifierUnavailable()
    {
        // The parent refuses to run un-hardened rather than running without PR_SET_DUMPABLE. That is unscorable, not
        // a zero -- the distinction R4 exists for.
        var gateway = new StubGateway(program => Completed(Marker(program, """{"status":"unavailable","reason":"prctl"}""")));

        var exception = await AssertEx.ThrowsAsync<BenchmarkExecutionException>(() =>
            VerifyAsync(gateway, Doubling, DoublingTests));

        AssertEx.True(exception.Message.StartsWith(BenchmarkRunJudgeStates.VerifierUnavailablePrefix, StringComparison.Ordinal),
            $"the message must carry the unavailable prefix the ranking reads; it was: {exception.Message}");
        AssertEx.Contains(exception.Message, "prctl");
    }

    [Test]
    public async Task PythonTests_ComputeDisabled_FailsJudging_NeverScoresZero()
    {
        var gateway = new StubGateway(_ => ComputeExecutionOutcome.Refused(ComputeRefusalCodes.ComputeDisabled,
            "The Python compute tool is disabled on this node (Compute:Enabled=false)."));

        var exception = await AssertEx.ThrowsAsync<BenchmarkExecutionException>(() =>
            VerifyAsync(gateway, Doubling, DoublingTests));

        AssertEx.True(exception.Message.StartsWith(BenchmarkRunJudgeStates.VerifierUnavailablePrefix, StringComparison.Ordinal));
        AssertEx.Contains(exception.Message, "Compute:Enabled=false");
    }

    [Test]
    public async Task PythonTests_SandboxCannotIsolate_FailsJudging()
    {
        var gateway = new StubGateway(_ => ComputeExecutionOutcome.Refused(ComputeRefusalCodes.NoIsolation,
            "run_python rejected: this node cannot isolate the compute sandbox filesystem, and the tool never runs a script that could read or write the rest of the machine."));

        var exception = await AssertEx.ThrowsAsync<BenchmarkExecutionException>(() =>
            VerifyAsync(gateway, Doubling, DoublingTests));

        AssertEx.Contains(exception.Message, "isolate");
    }

    [Test]
    public async Task PythonTests_ResourceLimitsUnenforceable_FailsJudging_VerifierUnavailable()
    {
        var gateway = new StubGateway(_ => ComputeExecutionOutcome.Refused(ComputeRefusalCodes.NoResourceLimits,
            "run_python rejected: this node's sandbox cannot enforce CPU, memory and process ceilings, and unattended execution is not run without them."));

        var exception = await AssertEx.ThrowsAsync<BenchmarkExecutionException>(() =>
            VerifyAsync(gateway, Doubling, DoublingTests));

        AssertEx.True(exception.Message.StartsWith(BenchmarkRunJudgeStates.VerifierUnavailablePrefix, StringComparison.Ordinal));
        AssertEx.True(gateway.RequiredResourceLimits,
            "unattended execution of operator test code must ASK for enforceable ceilings; that is why the refusal exists");
    }

    [Test]
    public async Task PythonTests_ChildProgramContainsNeitherNonceNorTestCode()
    {
        // A source-level assertion on the generated child. The cheapest guard against the future "just interpolate it,
        // it's simpler" tidy-up that reintroduces the defeat the two-process split exists to close.
        var gateway = new StubGateway(_ => Completed(string.Empty));
        _ = await VerifyAsync(gateway, Doubling, "assert solve(1) == 2  # SENTINEL_TEST_TEXT\n");
        var parent = AssertEx.NotNull(gateway.LastProgram);
        var nonce = NonceOf(parent);
        var child = BenchmarkPythonTestsHarness.ComposeChildProgram(Doubling);

        AssertEx.False(child.Contains(nonce, StringComparison.Ordinal), "the child must not hold the nonce");
        AssertEx.False(child.Contains("SENTINEL_TEST_TEXT", StringComparison.Ordinal), "the child must not hold the operator's tests");
        AssertEx.False(child.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("SENTINEL_TEST_TEXT")), StringComparison.Ordinal),
            "nor a base64 of them");
        AssertEx.Contains(child, "_CANDIDATE_B64 = \"", message: "the candidate reaches the child as base64 and nothing else");
    }

    [Test]
    public async Task PythonTests_ParentSetsPrSetDumpableBeforeSpawning()
    {
        var gateway = new StubGateway(_ => Completed(string.Empty));
        _ = await VerifyAsync(gateway, Doubling, DoublingTests);
        var parent = AssertEx.NotNull(gateway.LastProgram);

        var prctl = parent.IndexOf("prctl(4, 0, 0, 0, 0)", StringComparison.Ordinal);
        var spawn = parent.IndexOf("subprocess.Popen", StringComparison.Ordinal);

        AssertEx.True(prctl >= 0, "the parent must call PR_SET_DUMPABLE");
        AssertEx.True(spawn >= 0, "the parent must spawn the child");
        AssertEx.True(prctl < spawn, "hardening after the spawn would harden nothing");
    }

    [Test]
    public async Task PythonTests_ConfigTimeout_ClampedToComputeOptions()
    {
        // Can only tighten. A criterion is not allowed to buy itself more wall clock than the operator granted the
        // compute tool on this node.
        var gateway = new StubGateway(_ => Completed(string.Empty));
        _ = await VerifyAsync(gateway, Doubling, DoublingTests, timeoutSeconds: 300, computeTimeoutSeconds: 12);
        var parent = AssertEx.NotNull(gateway.LastProgram);

        AssertEx.Equal(expected: 12, ConfiguredTimeoutOf(parent));
    }

    [Test]
    public async Task PythonTests_AnswerWithNoCode_FailsWithoutTouchingTheSandbox()
    {
        var gateway = new StubGateway(_ => Completed(string.Empty));

        var result = await VerifyAsync(gateway, "   ", DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Null(gateway.LastProgram, "there was nothing to run");
    }

    [Test]
    public void CodeExtraction_FencedPythonBeforeAnyFenceBeforeWholeText()
    {
        const string TwoFences = """
                                 Here is some shell first:

                                 ```bash
                                 echo hello
                                 ```

                                 and the solution:

                                 ```python
                                 def solve(n):
                                     return n
                                 ```
                                 """;

        AssertEx.Contains(BenchmarkPythonCodeExtraction.Extract(TwoFences, mode: null), "def solve");
        AssertEx.Contains(BenchmarkPythonCodeExtraction.Extract("```\ndef solve(n):\n    return n\n```", mode: null), "def solve");
        AssertEx.Equal("def solve(n):\n    return n", BenchmarkPythonCodeExtraction.Extract("def solve(n):\n    return n", mode: null));
        AssertEx.Contains(BenchmarkPythonCodeExtraction.Extract(TwoFences, BenchmarkPythonCodeExtraction.WholeText), "```bash",
            message: "wholeText takes the answer verbatim, fences and all");
    }

    [Test]
    public void HarnessTemplatesCarryNoMultiLineStringLiteral()
    {
        // The precondition the composition's blank-line/comment stripping is sound under: a dropped blank line inside
        // a multi-line literal would change a VALUE rather than only the layout.
        var child = BenchmarkPythonTestsHarness.ComposeChildProgram("pass\n");

        AssertEx.False(child.Contains("\"\"\"", StringComparison.Ordinal), "the child template must carry no triple-quoted literal");
        AssertEx.False(child.Contains("'''", StringComparison.Ordinal));
    }

    private static string Marker(string parentProgram, string payload) =>
        BenchmarkPythonTestsHarness.MarkerPrefix + NonceOf(parentProgram) + ">>>" + payload + "\n";

    private static string NonceOf(string parentProgram)
    {
        const string Declaration = "_NONCE = \"";
        var start = parentProgram.IndexOf(Declaration, StringComparison.Ordinal) + Declaration.Length;
        return parentProgram[start..parentProgram.IndexOf('"', start)];
    }

    private static int ConfiguredTimeoutOf(string parentProgram)
    {
        const string Declaration = "_CONFIG_B64 = \"";
        var start = parentProgram.IndexOf(Declaration, StringComparison.Ordinal) + Declaration.Length;
        var encoded = parentProgram[start..parentProgram.IndexOf('"', start)];
        using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
        return document.RootElement.GetProperty("callTimeoutSeconds").GetInt32();
    }

    private static ComputeExecutionOutcome Completed(string standardOutput) =>
        ComputeExecutionOutcome.Executed(new SandboxCommandResult
        {
            ExecutionId = "x",
            ExitCode = 0,
            Completed = true,
            StandardOutput = standardOutput
        });

    private static async Task<BenchmarkJudgeVerifierResultV1> RunLocallyAsync(string candidate,
        string testCode,
        IReadOnlyList<string>? exports = null,
        int timeoutSeconds = 20)
    {
        return await VerifyAsync(new LocalInterpreterGateway(), candidate, testCode, exports, timeoutSeconds);
    }

    private static async Task<BenchmarkJudgeVerifierResultV1> VerifyAsync(IComputeToolGateway gateway,
        string candidate,
        string testCode,
        IReadOnlyList<string>? exports = null,
        int timeoutSeconds = 20,
        int computeTimeoutSeconds = 60)
    {
        var verifier = new BenchmarkPythonTestsVerifier(gateway,
            Options.Create(new ComputeOptions
            {
                Enabled = true,
                TimeoutSeconds = computeTimeoutSeconds
            }),
            NullLogger<BenchmarkPythonTestsVerifier>.Instance);
        var config = JsonSerializer.Serialize(new
        {
            testCode,
            exports = exports ?? ["solve"],
            timeoutSeconds
        });
        return await verifier.VerifyAsync(
            new BenchmarkJudgeRubricCriterionV1("solution", "Solution", "The code passes the hidden tests.", 100,
                BenchmarkJudgeCriterionKinds.PythonTests, config),
            candidate);
    }

    /// <summary>A gateway double that records the composed program and answers with whatever the test decided.</summary>
    private sealed class StubGateway : IComputeToolGateway
    {
        private readonly Func<string, ComputeExecutionOutcome> _outcome;

        public StubGateway(Func<string, ComputeExecutionOutcome> outcome)
        {
            _outcome = outcome;
        }

        public string? LastProgram { get; private set; }

        public bool RequiredResourceLimits { get; private set; }

        public Task<string> ExecuteAsync(ComputeRunToolRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the verifier uses the structured projection");

        public Task<ComputeExecutionOutcome> ExecuteDetailedAsync(ComputeRunToolRequest request,
            bool requireResourceLimits,
            CancellationToken cancellationToken = default)
        {
            LastProgram = request.Code;
            RequiredResourceLimits = requireResourceLimits;
            return Task.FromResult(_outcome(request.Code!));
        }
    }

    /// <summary>
    ///     Runs the composed parent through a REAL interpreter — no jail, no ceilings, no network unshare. It exists so
    ///     the adversarial rows exercise the process boundary rather than a canned string; the jail's own guarantees
    ///     are proven in the live suite, against the same candidates.
    /// </summary>
    private sealed class LocalInterpreterGateway : IComputeToolGateway
    {
        public Task<string> ExecuteAsync(ComputeRunToolRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the verifier uses the structured projection");

        public async Task<ComputeExecutionOutcome> ExecuteDetailedAsync(ComputeRunToolRequest request,
            bool requireResourceLimits,
            CancellationToken cancellationToken = default)
        {
            var interpreter = ResolveInterpreter();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(interpreter, ["-I", "-"])
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            _ = process.Start();
            var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(request.Code);
            process.StandardInput.Close();

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var completed = true;
            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                completed = false;
            }

            return ComputeExecutionOutcome.Executed(new SandboxCommandResult
            {
                ExecutionId = "local",
                ExitCode = completed ? process.ExitCode : -1,
                Completed = completed,
                StandardOutput = await standardOutput,
                StandardError = await standardError
            });
        }

        private static string ResolveInterpreter()
        {
            var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                             .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                             .Select(directory => Path.Combine(directory, "python3"));
            return candidates.FirstOrDefault(File.Exists)
                   ?? throw new SkipTestException("no python3 on PATH, so the generated harness cannot be executed here.");
        }
    }
}
