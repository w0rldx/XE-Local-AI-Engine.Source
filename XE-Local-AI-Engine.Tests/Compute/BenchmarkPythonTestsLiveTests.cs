namespace XE_Local_AI_Engine.Tests.Compute;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>pythonTests</c> adversarial set against the REAL jail: the real uv-provisioned interpreter, the real
///     bwrap chain, the real gateway, the real two-process harness.
///     <para>
///         It exists because a gateway substitute proves the PARSER, not the BOUNDARY. A substitute returns whatever
///         the test author decided a sandbox returns, so it can show that C# scores a marker-less result 0; it cannot
///         show that a candidate is unable to produce a marker. Every row here reproduces one line of the
///         "what the candidate can do" table as a real candidate in a real jail and asserts it does not score.
///     </para>
///     <para>
///         Opt-in on <c>XE_COMPUTE_LIVE=1</c> and a root-owned <c>bwrap</c>, the same gate
///         <see cref="ComputeSandboxLiveTests" /> uses, and for the same reason: the first run provisions a real venv.
///         Once the host has the mechanism, anything else FAILS rather than skips.
///     </para>
/// </summary>
public sealed class BenchmarkPythonTestsLiveTests : IDisposable
{
    private const string EnabledVariable = "XE_COMPUTE_LIVE";

    private const string DoublingTests = """
                                         assert solve(10) == 20
                                         assert solve(0) == 0
                                         """;

    private readonly ComputePythonEnvironment _environment;
    private readonly HttpClient _httpClient = new();

    public BenchmarkPythonTestsLiveTests()
    {
        _environment = new ComputePythonEnvironment(_httpClient, NullLogger<ComputePythonEnvironment>.Instance);
    }

    public void Dispose()
    {
        _environment.Dispose();
        _httpClient.Dispose();
    }

    [Test]
    public async Task PythonTests_CorrectSolution_ScoresInTheRealJail()
    {
        var result = await VerifyAsync("def solve(n):\n    return n * 2\n", DoublingTests);

        AssertEx.True(result.Passed, $"a correct solution must score in the real jail; detail was: {result.Detail}");
    }

    [Test]
    public async Task PythonTests_BrokenSolution_ScoresZeroInTheRealJail()
    {
        var result = await VerifyAsync("def solve(n):\n    return n * 3\n", DoublingTests);

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateWalksAncestorFramesForANonce_Scores0InTheRealJail()
    {
        var result = await VerifyAsync("""
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

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateWritesForgedMarkerToSysDunderStdout_Scores0InTheRealJail()
    {
        var result = await VerifyAsync("""
                                       import sys
                                       sys.__stdout__.write('<<<XE-PYTEST:deadbeef>>>{"status":"verdict","collected":1,"passed":1,"failed":0}\n')
                                       sys.__stdout__.flush()


                                       def solve(n):
                                           return 0
                                       """, DoublingTests);

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateCallsOsUnderscoreExitZero_Scores0InTheRealJail()
    {
        var result = await VerifyAsync("import os\nos._exit(0)\n", DoublingTests);

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateCallsSysExitZero_Scores0InTheRealJail()
    {
        var result = await VerifyAsync("import sys\nsys.exit(0)\n", DoublingTests);

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateSigkillsItself_Scores0InTheRealJail()
    {
        var result = await VerifyAsync("import os, signal\nos.kill(os.getpid(), signal.SIGKILL)\n", DoublingTests);

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateSigkillsTheParent_ProducesNoMarker_Scores0()
    {
        // The namespace-teardown path, and the one row that ONLY the real jail can prove: the harness is PID 1 of the
        // jail's PID namespace, so killing it tears the namespace down and the command returns with no stdout at all.
        var result = await VerifyAsync("""
                                       import os, signal
                                       os.kill(os.getppid(), signal.SIGKILL)


                                       def solve(n):
                                           return 0
                                       """, DoublingTests);

        AssertEx.False(result.Passed, "denying the verdict is a failure, never a pass");
    }

    [Test]
    public async Task PythonTests_CandidateInfiniteLoop_Scores0InTheRealJail()
    {
        var result = await VerifyAsync("""
                                       def solve(n):
                                           while True:
                                               pass
                                       """, DoublingTests, criterionTimeoutSeconds: 5, computeTimeoutSeconds: 60);

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateMonkeyPatchesBuiltinsAndUnittest_Scores0InTheRealJail()
    {
        var result = await VerifyAsync("""
                                       import builtins, json, sys, unittest
                                       builtins.print = lambda *a, **k: None
                                       json.dumps = lambda *a, **k: '{}'
                                       sys.modules['unittest'] = None


                                       def solve(n):
                                           return n * 2
                                       """, "assert solve(10) == 21\n");

        AssertEx.False(result.Passed);
    }

    [Test]
    public async Task PythonTests_CandidateContainingTripleQuotesAndBackslashes_CannotEscapeIntoTheParent_InTheRealJail()
    {
        var result = await VerifyAsync("def solve(n):\n    return \"\"\"\\   ''' \"\"\" and n\n", DoublingTests);

        AssertEx.False(result.Passed);
        AssertEx.Contains(result.Detail, "collected cases passed", message: "the harness must have run normally, not been escaped into");
    }

    [Test]
    public async Task PythonTests_PyEvalRunsSetupInTheChild_InTheRealJail()
    {
        var result = await VerifyAsync("""
                                       class Solver:
                                           def __init__(self, start):
                                               self.value = start

                                           def step(self):
                                               self.value += 1
                                       """, "assert pyeval('o = Solver(3)\\no.step()\\no.value') == 4\n", exports: []);

        AssertEx.True(result.Passed, $"detail was: {result.Detail}");
    }

    [Test]
    public async Task PythonTests_ComputeDisabled_IsUnscorable_NotZero()
    {
        // The kill-switch, live: when the sandbox cannot be trusted, the run is unranked with a named reason rather
        // than the model being told it wrote failing code.
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var verifier = CreateVerifier(provider, enabled: false, computeTimeoutSeconds: 60);

        var exception = await AssertEx.ThrowsAsync<BenchmarkExecutionException>(() =>
            verifier.VerifyAsync(Criterion(DoublingTests, ["solve"], timeoutSeconds: 20), "def solve(n):\n    return n * 2\n"));

        AssertEx.Contains(exception.Message, "verifier-unavailable");
        AssertEx.Contains(exception.Message, "Compute:Enabled=false");
    }

    private async Task<BenchmarkJudgeVerifierResultV1> VerifyAsync(string candidate,
        string testCode,
        IReadOnlyList<string>? exports = null,
        int criterionTimeoutSeconds = 20,
        int computeTimeoutSeconds = 90)
    {
        RequireIsolationCapableHost();
        using var provider = CreateHostProvider();
        var verifier = CreateVerifier(provider, enabled: true, computeTimeoutSeconds);
        return await verifier.VerifyAsync(Criterion(testCode, exports ?? ["solve"], criterionTimeoutSeconds), candidate);
    }

    private static BenchmarkJudgeRubricCriterionV1 Criterion(string testCode, IReadOnlyList<string> exports, int timeoutSeconds) =>
        new("solution",
            "Solution",
            "The code passes the hidden tests.",
            100,
            BenchmarkJudgeCriterionKinds.PythonTests,
            JsonSerializer.Serialize(new
            {
                testCode,
                exports,
                timeoutSeconds
            }));

    private BenchmarkPythonTestsVerifier CreateVerifier(IAgentSandboxRuntimeProvider provider, bool enabled, int computeTimeoutSeconds)
    {
        var options = new ComputeOptions
        {
            Enabled = enabled,
            TimeoutSeconds = computeTimeoutSeconds
        };
        var gateway = new ComputeToolGateway(provider,
            new StubIdentityProvider(),
            _environment,
            Options.Create(options),
            Options.Create(new LocalContainerOptions()),
            NullLogger<ComputeToolGateway>.Instance);
        return new BenchmarkPythonTestsVerifier(gateway, Options.Create(options), NullLogger<BenchmarkPythonTestsVerifier>.Instance);
    }

    private static ProcessSandboxRuntimeProvider CreateHostProvider() =>
        new(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System);

    /// <summary>Skips only when the host has no mechanism at all, and FAILS when it has one but the boundary does not hold.</summary>
    private static void RequireIsolationCapableHost()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("the compute runtime is Linux-only, and so is pythonTests.");
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            throw new SkipTestException($"set {EnabledVariable}=1 to allow this suite to provision the pinned compute venv and spawn real processes.");
        }

        if (TrustedBinaryResolver.Resolve("bwrap") is null)
        {
            throw new SkipTestException("this host has no root-owned bwrap under /usr/bin, /bin or /usr/local/bin.");
        }

        var containment = new HostSandboxContainmentProbe().Containment;
        if (!containment.SupportsFilesystemIsolation)
        {
            AssertEx.True(condition: false,
                $"this host has a trusted bwrap, so the compute filesystem boundary must hold; the probe reported: {containment.FilesystemIsolationUnavailableReason}");
        }
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentHomeOwnerIdentity("owner-live", "node-live"));
    }
}
