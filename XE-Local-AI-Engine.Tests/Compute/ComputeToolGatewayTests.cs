namespace XE_Local_AI_Engine.Tests.Compute;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Gateway behavior against a recording sandbox provider: what it ASKS the sandbox for (its own runtime profile, an
///     isolated interpreter invocation, egress denial and ceilings only where the provider advertises them) and how it
///     renders what comes back. The real containment is exercised live in
///     <see cref="ComputeSandboxLiveTests" />; this suite pins the request shape and the result vocabulary, which is
///     what a model actually reads.
/// </summary>
public sealed class ComputeToolGatewayTests
{
    [Test]
    public async Task ExecuteAsync_RunsTheProvisionedInterpreterOnItsOwnJail_ReadingTheScriptFromStandardInput()
    {
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.SupportsNetworkPolicy
                                                    | SandboxProviderCapabilities.SupportsResourceLimits);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(ComputeToolGateway.RuntimeProfile, create.RuntimeProfile);
        // The attach key carries the profile plus this invocation's id (see the concurrency test below); the profile
        // PREFIX is what keeps the jail keyed apart from AgentHome's.
        AssertEx.True(create.AttachKey.RuntimeProfile.StartsWith(ComputeToolGateway.RuntimeProfile, StringComparison.Ordinal),
            "the attach key must stay within this tool's runtime profile");
        AssertEx.NotEqual("dotnet-agent-home", create.AttachKey.RuntimeProfile,
            "the compute jail must be keyed apart from AgentHome's, or a script could reach a staged workspace");

        var command = AssertEx.NotNull(provider.CommandRequest);
        AssertEx.Equal("/provisioned/python", command.Executable);
        AssertEx.Equal(expected: 2, command.Arguments.Count);
        AssertEx.Equal("-I", command.Arguments[0], "isolated mode keeps the import surface the provisioned closure");
        AssertEx.Equal("-", command.Arguments[1]);
        AssertEx.Equal("print(1)", command.StandardInput, "the script is piped, never written to disk or placed in argv");
    }

    [Test]
    public async Task ExecuteAsync_TearsDownEveryWritableSurfaceAfterTheCall()
    {
        // The tool advertises itself to the model as stateless. Both places a script can leave a file — the jail it
        // ran in and the HOME/TMPDIR scratch it was pointed at — must therefore be per call and gone afterwards, or
        // one conversation's files are readable by the next.
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });
        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(2)" });

        AssertEx.Equal(expected: 2, provider.KilledSandboxIds.Count, "each call must terminate the jail it ran in");
        AssertEx.Equal(expected: 2, provider.CommandRequests.Count);

        var first = provider.CommandRequests[0].Environment!["HOME"];
        var second = provider.CommandRequests[1].Environment!["HOME"];
        AssertEx.NotEqual(first, second, "a shared scratch directory would carry one script's files into the next call");
        AssertEx.False(Directory.Exists(first), "the scratch directory must not outlive the call that used it");
        AssertEx.False(Directory.Exists(second));
        AssertEx.Equal(first, provider.CommandRequests[0].Environment!["TMPDIR"], "HOME and TMPDIR share the one per-call directory");
    }

    [Test]
    public async Task ExecuteAsync_KeysEveryInvocationToItsOwnJail()
    {
        // The registry attaches BY the attach key, so a constant one handed two overlapping calls a single live jail:
        // one shared working directory between unrelated conversations, and — now that teardown is per call — whichever
        // finished first killing the jail out from under the other.
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });
        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(2)" });

        AssertEx.Equal(expected: 2, provider.CreateRequests.Count);
        AssertEx.NotEqual(provider.CreateRequests[0].AttachKey, provider.CreateRequests[1].AttachKey,
            "two invocations must never share an attach key, or the registry hands them one jail");
        AssertEx.Equal(provider.CreateRequests[0].RuntimeProfile, provider.CreateRequests[1].RuntimeProfile,
            "only the KEY varies per call; the profile the jail is built from is the same shape every time");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheScriptFails_StillTearsDownTheJail()
    {
        // A failed or cancelled run is exactly when a script is most likely to have left something behind, so the
        // teardown cannot sit on the success path.
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None)
        {
            Result = Completed(exitCode: 1, standardOutput: string.Empty, standardError: "boom")
        };
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "raise SystemExit(1)" });

        AssertEx.Equal(expected: 1, provider.KilledSandboxIds.Count);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheProviderAdvertisesContainment_RequestsEgressDenialAndCeilings()
    {
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.SupportsNetworkPolicy
                                                    | SandboxProviderCapabilities.SupportsResourceLimits);
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            MemoryMb = 512,
            CpuCount = 1,
            PidsLimit = 32
        });

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(SandboxNetworkPolicy.None, create.NetworkPolicy);
        var limits = AssertEx.NotNull(create.ResourceLimits);
        AssertEx.Equal(expected: 512, limits.MemoryMb!.Value);
        AssertEx.Equal(expected: 1d, limits.CpuCount!.Value);
        AssertEx.Equal(expected: 32, limits.PidsLimit!.Value);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheHostCannotContain_AsksForNothingItCannotGet()
    {
        // The provider FAILS CLOSED on a guarantee it cannot honor, so asking unconditionally would not harden the tool
        // — it would make it unusable on every host without user namespaces or a systemd user scope. Asking for exactly
        // what is advertised keeps the guarantee real where it exists, with the degradation visible in the containment
        // log rather than silent.
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(SandboxNetworkPolicy.Unrestricted, create.NetworkPolicy);
        AssertEx.Null(create.ResourceLimits);
    }

    [Test]
    public async Task ExecuteAsync_RendersExitCodeStdoutAndStderr()
    {
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None)
        {
            Result = Completed(exitCode: 1, standardOutput: "4\n", standardError: "boom\n")
        };
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });

        AssertEx.Contains(rendered, "exit_code: 1");
        AssertEx.Contains(rendered, "stdout:\n4\n");
        AssertEx.Contains(rendered, "stderr:\nboom\n");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheScriptDidNotComplete_SaysSoRatherThanReportingAPlainExitCode()
    {
        // A timed-out run comes back Completed=false with exit code -1. Rendering that as a bare "exit_code: -1" would
        // read to a model as a normal failing program, and it would try to debug a script that never finished.
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None)
        {
            Result = new SandboxCommandResult
            {
                ExecutionId = "x",
                ExitCode = -1,
                Completed = false
            }
        };
        var gateway = CreateGateway(provider, new ComputeOptions { TimeoutSeconds = 7 });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "while True: pass" });

        AssertEx.Contains(rendered, "did not finish within 7s");
        AssertEx.Contains(rendered, "terminated");
    }

    [Test]
    public async Task ExecuteAsync_WhenOutputExceedsTheCap_TruncatesWithTheSharedMarker()
    {
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None)
        {
            Result = Completed(exitCode: 0, standardOutput: new string('a', 500), standardError: string.Empty)
        };
        var gateway = CreateGateway(provider, new ComputeOptions { MaxOutputBytes = 100 });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print('a' * 500)" });

        AssertEx.Contains(rendered, "…[output truncated]");
        AssertEx.False(rendered.Contains(new string('a', 200), StringComparison.Ordinal),
            "the capped stream must not carry more than the configured budget");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheProviderItselfTruncated_KeepsTheMarker()
    {
        // The sandbox caps capture at its own 4 MiB ceiling before this gateway ever sees the bytes, so a stream can be
        // short enough to pass our budget and still be incomplete. Dropping the marker there would tell a model it had
        // the whole output.
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None)
        {
            Result = Completed(exitCode: 0, standardOutput: "head", standardError: string.Empty) with
            {
                StandardOutputTruncated = true
            }
        };
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });

        AssertEx.Contains(rendered, "…[output truncated]");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheEnvironmentCannotBeProvisioned_ReturnsTheModelSafeReasonWithoutTouchingTheSandbox()
    {
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None);
        var gateway = CreateGateway(provider,
            environment: new StubEnvironment(new ComputeEnvironmentException("The Python compute tool is available on Linux only.")));

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" });

        AssertEx.Contains(rendered, "Linux only");
        AssertEx.Null(provider.CreateRequest, "a failed provision must not create a jail");
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_Throws()
    {
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.None);
        var gateway = CreateGateway(provider);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            gateway.ExecuteAsync(new ComputeRunToolRequest { Code = "print(1)" }, cancellationTokenSource.Token));
    }

    private static SandboxCommandResult Completed(int exitCode, string standardOutput, string standardError)
    {
        return new SandboxCommandResult
        {
            ExecutionId = "x",
            ExitCode = exitCode,
            Completed = true,
            StandardOutput = standardOutput,
            StandardError = standardError
        };
    }

    private static ComputeToolGateway CreateGateway(IAgentSandboxRuntimeProvider provider,
        ComputeOptions? options = null,
        IComputePythonEnvironment? environment = null)
    {
        return new ComputeToolGateway(provider,
            new StubIdentityProvider(),
            environment ?? new StubEnvironment("/provisioned/python"),
            Options.Create(options ?? new ComputeOptions()),
            NullLogger<ComputeToolGateway>.Instance);
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity("owner-1", "node-1"));
        }
    }

    private sealed class StubEnvironment : IComputePythonEnvironment
    {
        private readonly ComputeEnvironmentException? _failure;
        private readonly string _interpreter;

        public StubEnvironment(string interpreter)
        {
            _interpreter = interpreter;
        }

        public StubEnvironment(ComputeEnvironmentException failure)
        {
            _interpreter = string.Empty;
            _failure = failure;
        }

        public Task<string> GetInterpreterPathAsync(CancellationToken cancellationToken = default)
        {
            return _failure is not null ? Task.FromException<string>(_failure) : Task.FromResult(_interpreter);
        }
    }

    /// <summary>Records what the gateway asked for and answers with a canned result; no process is ever spawned.</summary>
    private sealed class RecordingSandboxProvider : IAgentSandboxRuntimeProvider
    {
        public RecordingSandboxProvider(SandboxProviderCapabilities capabilities)
        {
            Capabilities = capabilities;
        }

        public SandboxCreateRequest? CreateRequest { get; private set; }

        public SandboxCommandRequest? CommandRequest { get; private set; }

        public List<SandboxCreateRequest> CreateRequests { get; } = [];

        public List<SandboxCommandRequest> CommandRequests { get; } = [];

        public List<string> KilledSandboxIds { get; } = [];

        public SandboxCommandResult Result { get; init; } = new()
        {
            ExecutionId = "x",
            ExitCode = 0,
            Completed = true
        };

        public string ProviderName => "recording";

        public SandboxProviderCapabilities Capabilities { get; }

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            CreateRequest = request;
            CreateRequests.Add(request);
            return Task.FromResult(new SandboxHandle
            {
                ProviderName = ProviderName,
                SandboxId = "sandbox-" + CreateRequests.Count,
                AttachKey = request.AttachKey,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = request.AttachKey.ManifestVersion
            });
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default)
        {
            CommandRequest = request;
            CommandRequests.Add(request);
            return Task.FromResult(Result with { ExecutionId = request.ExecutionId });
        }

        public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ResetDirectoryAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
        {
            KilledSandboxIds.Add(handle.SandboxId);
            return Task.CompletedTask;
        }
    }
}
