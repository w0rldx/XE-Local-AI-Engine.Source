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
