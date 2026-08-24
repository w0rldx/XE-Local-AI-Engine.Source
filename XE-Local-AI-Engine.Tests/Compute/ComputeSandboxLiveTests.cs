namespace XE_Local_AI_Engine.Tests.Compute;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <para>
///         LIVE end-to-end coverage of the compute tool: the real uv-provisioned interpreter, the real process sandbox,
///         the real gateway. It proves the properties the unit suites cannot — that the pinned closure really imports,
///         that egress really is denied inside the jail, and that a non-terminating script really is killed at the
///         timeout rather than hanging the turn.
///     </para>
///     <para>
///         <b>Opt-in, and it SKIPS rather than passing.</b> The first run provisions a real venv (a network download of
///         tens of megabytes), which is not something an ordinary suite run should do, so it is gated on
///         <c>XE_COMPUTE_LIVE=1</c>. The containment assertions skip again when the host has no mechanism to enforce
///         them: a containment test that silently goes green on a box that cannot contain anything reports a guarantee
///         nothing exercised, which is worse than no test at all.
///     </para>
/// </summary>
public sealed class ComputeSandboxLiveTests : IDisposable
{
    /// <summary>Set to <c>1</c> to allow this suite to provision the pinned compute venv and spawn real processes.</summary>
    private const string EnabledVariable = "XE_COMPUTE_LIVE";

    // One environment for the whole class, so the venv provision is paid once rather than per test. Constructed even on
    // a skipped run (it does nothing until an interpreter is asked for), which keeps the disposal contract simple.
    private readonly HttpClient _httpClient = new();
    private readonly ComputePythonEnvironment _environment;

    public ComputeSandboxLiveTests()
    {
        _environment = new ComputePythonEnvironment(_httpClient, NullLogger<ComputePythonEnvironment>.Instance);
    }

    public void Dispose()
    {
        _environment.Dispose();
        _httpClient.Dispose();
    }

    [Test]
    public async Task RunPython_ImportsThePinnedClosure_AndReturnsWhatTheScriptPrinted()
    {
        RequireOptIn();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import numpy, scipy, sympy
                   x = sympy.symbols('x')
                   print(sympy.integrate(2 * x, x))
                   print(numpy.array([1, 2, 3]).sum())
                   """
        });

        AssertEx.Contains(rendered, "exit_code: 0");
        // sympy must be importable AND usable — this is what proves the venv provision end to end.
        AssertEx.Contains(rendered, "x**2");
        AssertEx.Contains(rendered, "6");
    }

    [Test]
    public async Task RunPython_WhenTheScriptRaises_ReportsANonZeroExitAndTheTraceback()
    {
        // The failure path is a feature, not an edge case: the persona's instructions tell the model to read the
        // traceback and fix its script, which only works if the traceback actually reaches it on stderr.
        RequireOptIn();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "raise ValueError('nope')" });

        AssertEx.False(rendered.Contains("exit_code: 0", StringComparison.Ordinal), "a raising script must not report success");
        AssertEx.Contains(rendered, "ValueError");
        AssertEx.Contains(rendered, "nope");
    }

    [Test]
    public async Task RunPython_WhenTheScriptDoesNotTerminate_IsKilledAtTheTimeout()
    {
        RequireOptIn();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider, new ComputeOptions { TimeoutSeconds = 5 });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "while True: pass" });

        AssertEx.Contains(rendered, "did not finish within 5s");
        AssertEx.Contains(rendered, "terminated");
    }

    [Test]
    public async Task RunPython_WhenOutputIsHuge_IsTruncatedWithTheMarker()
    {
        RequireOptIn();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider, new ComputeOptions { MaxOutputBytes = 2048 });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print('a' * 200000)" });

        AssertEx.Contains(rendered, "…[output truncated]");
        AssertEx.True(rendered.Length < 20000, "a runaway print must not reach the model in full");
    }

    [Test]
    public async Task RunPython_CannotSeeWhatAnEarlierCallWrote()
    {
        // The advertised contract is that a call leaves nothing behind. ONE provider and ONE gateway across both calls
        // is the point: that is the shape a real turn has, and the shape that used to reattach to the same jail and the
        // same scratch directory, handing the second script the first one's files.
        RequireOptIn();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        // Both the working directory (the jail) and HOME (the scratch) — the two writable surfaces a script has.
        var wrote = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import os, pathlib
                   pathlib.Path("leaked-cwd.txt").write_text("from call one")
                   pathlib.Path(os.environ["HOME"], "leaked-home.txt").write_text("from call one")
                   print("WROTE")
                   """
        });
        AssertEx.Contains(wrote, "exit_code: 0");
        AssertEx.Contains(wrote, "WROTE");

        var read = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import os, pathlib
                   print("CWD", pathlib.Path("leaked-cwd.txt").exists())
                   print("HOME", pathlib.Path(os.environ["HOME"], "leaked-home.txt").exists())
                   """
        });

        AssertEx.Contains(read, "exit_code: 0");
        AssertEx.Contains(read, "CWD False");
        AssertEx.Contains(read, "HOME False");
    }

    [Test]
    public async Task RunPython_ConcurrentCalls_GetTheirOwnJailAndSurviveEachOther()
    {
        // Two genuinely overlapping invocations through ONE gateway — the shape a research loop produces. With a
        // constant attach key the registry handed both the SAME live jail: they shared a working directory, and the
        // first to finish killed it under the one still running. Each script writes a file named after itself, sleeps
        // past the other's start, then lists its own directory, so a shared jail shows up as the sibling's file.
        RequireOptIn();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        Task<string> RunAsync(string tag)
        {
            return gateway.ExecuteAsync(new ComputeRunToolRequest
            {
                Code = $"""
                        import pathlib, time
                        pathlib.Path("{tag}.txt").write_text("{tag}")
                        time.sleep(2)
                        print("SAW", sorted(p.name for p in pathlib.Path(".").iterdir()))
                        """
            });
        }

        var results = await Task.WhenAll(RunAsync("alpha"), RunAsync("beta"));

        // Both must COMPLETE: under one shared jail the loser was torn down mid-run.
        AssertEx.Contains(results[0], "exit_code: 0");
        AssertEx.Contains(results[1], "exit_code: 0");
        AssertEx.Contains(results[0], "SAW ['alpha.txt']");
        AssertEx.Contains(results[1], "SAW ['beta.txt']");
    }

    [Test]
    public async Task RunPython_CannotReachTheNetwork()
    {
        RequireOptIn();
        var containment = new HostSandboxContainmentProbe().Containment;
        if (!containment.SupportsNetworkIsolation)
        {
            Skip($"this host cannot create an empty network namespace: {containment.NetworkIsolationUnavailableReason}");
            return;
        }

        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        // A connect to a LIVE local listener would be the strongest probe, but any egress at all is enough here and a
        // DNS-free literal keeps the failure unambiguous: with an empty netns even loopback is unreachable.
        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import socket
                   try:
                       socket.create_connection(("1.1.1.1", 53), timeout=3)
                       print("REACHED")
                   except OSError as error:
                       print("DENIED", type(error).__name__)
                   """
        });

        AssertEx.Contains(rendered, "DENIED");
        AssertEx.False(rendered.Contains("REACHED", StringComparison.Ordinal),
            "a script inside the compute jail must not reach the network");
    }

    [Test]
    public async Task RunPython_CannotWriteIntoTheProvisionedVenv()
    {
        // site-packages is imported by every later call, so a script that can drop a module there turns one approval
        // into code that runs on all the following ones. Second call proves the lockdown did not break the closure it
        // is protecting — a read-only venv that cannot import numpy would be a worse bug than the one being fixed.
        RequireOptIn();
        using var provider = CreateHostProvider();
        var gateway = CreateGateway(provider);

        var attempt = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = """
                   import pathlib, sysconfig
                   target = pathlib.Path(sysconfig.get_paths()["purelib"], "xe_trojan.py")
                   try:
                       target.write_text("import os")
                       print("WROTE")
                   except OSError as error:
                       print("DENIED", type(error).__name__)
                   """
        });

        AssertEx.Contains(attempt, "exit_code: 0");
        AssertEx.Contains(attempt, "DENIED");
        AssertEx.False(attempt.Contains("WROTE", StringComparison.Ordinal),
            "a script must not be able to drop a module into the venv every later call imports");

        var reuse = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "import numpy; print('IMPORT OK', numpy.ndarray)"
        });

        AssertEx.Contains(reuse, "exit_code: 0");
        AssertEx.Contains(reuse, "IMPORT OK");
    }

    private ComputeToolGateway CreateGateway(IAgentSandboxRuntimeProvider provider, ComputeOptions? options = null)
    {
        return new ComputeToolGateway(provider,
            new StubIdentityProvider(),
            _environment,
            Options.Create(options ?? new ComputeOptions()),
            NullLogger<ComputeToolGateway>.Instance);
    }

    private static ProcessSandboxRuntimeProvider CreateHostProvider()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System);
    }

    private static void RequireOptIn()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip("the compute runtime is Linux-only (the uv pin and the process runner both are).");
        }

        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal))
        {
            Skip($"set {EnabledVariable}=1 to allow this suite to provision the pinned compute venv and spawn real processes.");
        }
    }

    private static void Skip(string reason)
    {
        throw new SkipTestException(reason);
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity("owner-live", "node-live"));
        }
    }
}
