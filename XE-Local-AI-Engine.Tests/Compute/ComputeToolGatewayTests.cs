namespace XE_Local_AI_Engine.Tests.Compute;

using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Gateway behavior against a recording sandbox provider: what it ASKS the sandbox for (its own runtime profile, a
///     filesystem boundary with exactly two read-only trees, an isolated interpreter invocation, and ceilings only
///     where the provider advertises them) and how it renders what comes back. The real containment is exercised live
///     in <see cref="ComputeSandboxLiveTests" />; this suite pins the request shape and the result vocabulary, which is
///     what a model actually reads.
/// </summary>
public sealed class ComputeToolGatewayTests
{
    /// <summary>
    ///     What a host able to run the tool advertises. The filesystem boundary is the one flag whose absence refuses
    ///     the call outright, so every test that expects a script to run has to carry it.
    /// </summary>
    private const SandboxProviderCapabilities Contained =
        SandboxProviderCapabilities.SupportsFilesystemIsolation | SandboxProviderCapabilities.SupportsNetworkPolicy;

    [Test]
    public async Task ExecuteAsync_RunsTheProvisionedInterpreterOnItsOwnJail_ReadingTheScriptFromStandardInput()
    {
        var provider = new RecordingSandboxProvider(Contained | SandboxProviderCapabilities.SupportsResourceLimits);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(ComputeToolGateway.RuntimeProfile, create.RuntimeProfile);

        // Asserted against the DECLARATION rather than a literal, so this create site and SandboxWorkloads.RunPython
        // cannot drift apart: run_python is the ONE workload that asks for ceilings, and the operator-facing isolation
        // summary reports declaration-AND-capability as its "Resource limits" column. This provider advertises the
        // capability, so the request must carry them; the sibling tests using `Contained` alone do not, and the
        // gateway's own capability gate is what makes those null.
        AssertEx.Equal(SandboxWorkloads.RunPython.RequestsResourceLimits, create.ResourceLimits is not null);
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
    public async Task ExecuteAsync_TearsDownTheJailAfterEveryCall_AndReclaimsTheScratchInsideIt()
    {
        // The tool advertises itself to the model as stateless. The jail is the only place a script can write —
        // its HOME and TMPDIR are directories inside it — so one teardown per call is what makes that true, and a
        // jail that survived the call would carry one conversation's files into the next.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });
        var firstJail = AssertEx.NotNull(provider.LastJailRoot);
        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(2)"
        });
        var secondJail = AssertEx.NotNull(provider.LastJailRoot);

        AssertEx.Equal(expected: 2, provider.KilledSandboxIds.Count, "each call must terminate the jail it ran in");
        AssertEx.Equal(expected: 2, provider.CommandRequests.Count);
        AssertEx.NotEqual(firstJail, secondJail, "a shared jail would carry one script's files into the next call");
        AssertEx.False(Directory.Exists(firstJail), "the jail must not outlive the call that ran in it");
        AssertEx.False(Directory.Exists(secondJail));
        // Asked for through the provider rather than created behind its back, and asked for on BOTH calls: the two
        // directories the sandbox presents as HOME and TMPDIR have to exist before the interpreter starts.
        AssertEx.Equal(expected: 4, provider.ResetDirectories.Count);
        AssertEx.Contains(provider.ResetDirectories, "home");
        AssertEx.Contains(provider.ResetDirectories, ".tmp");
    }

    [Test]
    public async Task ExecuteAsync_PointsHomeAndTmpdirAtTheSandboxsOwnPaths_NotAtHostPaths()
    {
        // Under the filesystem boundary the jail is not present at its host name inside the namespace at all, so a
        // host path in the environment names nothing the script can reach. The values have to be the SANDBOX's view:
        // /work/home and /work's sibling /tmp, both backed by the jail the disk ceiling meters.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var jailRoot = AssertEx.NotNull(provider.LastJailRoot);
        var environment = AssertEx.NotNull(provider.CommandRequests[0].Environment);
        AssertEx.Equal(SandboxIsolatedPaths.Home, environment["HOME"]);
        AssertEx.Equal(SandboxIsolatedPaths.Temp, environment["TMPDIR"]);
        AssertEx.Equal(environment["TMPDIR"], environment["TMP"]);
        AssertEx.Equal(environment["TMPDIR"], environment["TEMP"]);
        AssertEx.NotEqual(environment["HOME"], environment["TMPDIR"],
            "HOME and TMPDIR are separate directories: a script clearing its temp files must not wipe its own home");

        string[] scratchVariables = ["HOME", "TMPDIR", "TMP", "TEMP"];
        foreach (var name in scratchVariables)
        {
            AssertEx.False(environment[name].StartsWith(jailRoot, StringComparison.Ordinal),
                $"{name} must be the in-sandbox path, not the host path the jail happens to have");
        }

        AssertEx.Equal("1", environment["PYTHONNOUSERSITE"]);
        AssertEx.Equal("1", environment["PYTHONDONTWRITEBYTECODE"]);
    }

    [Test]
    public async Task ExecuteAsync_LeavesTheThreadCountVariablesToTheSandbox_RatherThanNamingThemTwice()
    {
        // The pinning is derived from SandboxCreateRequest.ThreadLimit, which the create request already carries.
        // Naming the variables here as well would let the tool's environment and the sandbox's CPU ceiling drift
        // apart in exactly the situation the pinning exists to prevent — and the caller environment is emitted LAST,
        // so this side would silently win.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            ThreadLimit = 3
        });

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var environment = AssertEx.NotNull(provider.CommandRequests[0].Environment);
        foreach (var name in SandboxIsolatedChain.ThreadCountVariableNames)
        {
            AssertEx.False(environment.ContainsKey(name), $"{name} must come from the sandbox's thread limit, not from the gateway");
        }

        AssertEx.Equal(expected: 3, AssertEx.NotNull(provider.CreateRequest).ThreadLimit!.Value);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheProviderNamesNoJailRoot_RefusesRatherThanRunningWithUnmeteredScratch()
    {
        // Fails closed for the same reason the egress check does. A provider that cannot name the directory its
        // commands run in cannot be handed a scratch path inside it either, and the alternative — putting the scratch
        // back outside the jail — would quietly restore the hole this change closed.
        var provider = new RecordingSandboxProvider(Contained, namesAJailRoot: false);
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.Contains(rendered, "run_python rejected");
        AssertEx.Empty(provider.CommandRequests, "a jail with no nameable root must not run the script anyway");
        AssertEx.Equal(expected: 1, provider.KilledSandboxIds.Count, "the refusal must still tear down the jail it created");
    }

    [Test]
    public async Task ExecuteAsync_KeysEveryInvocationToItsOwnJail()
    {
        // The registry attaches BY the attach key, so a constant one handed two overlapping calls a single live jail:
        // one shared working directory between unrelated conversations, and — now that teardown is per call — whichever
        // finished first killing the jail out from under the other.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });
        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(2)"
        });

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
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 1, standardOutput: string.Empty, standardError: "boom")
        };
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "raise SystemExit(1)"
        });

        AssertEx.Equal(expected: 1, provider.KilledSandboxIds.Count);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheProviderAdvertisesContainment_RequestsEgressDenialAndCeilings()
    {
        var provider = new RecordingSandboxProvider(Contained | SandboxProviderCapabilities.SupportsResourceLimits);
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            MemoryMb = 512,
            CpuCount = 1,
            PidsLimit = 32
        });

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(SandboxNetworkPolicy.None, create.NetworkPolicy);
        var limits = AssertEx.NotNull(create.ResourceLimits);
        AssertEx.Equal(expected: 512, limits.MemoryMb!.Value);
        AssertEx.Equal(expected: 1d, limits.CpuCount!.Value);
        AssertEx.Equal(expected: 32, limits.PidsLimit!.Value);
    }

    [Test]
    public async Task ExecuteAsync_AsksForItsOwnJailDiskCeiling_RatherThanInheritingTheNodeWideOne()
    {
        // A script doing arithmetic writes almost nothing, so the node-wide allowance — sized for a workspace build —
        // is the wrong number for this jail. The request may only TIGHTEN it, so no capability gate is needed: a
        // provider that ignores the field is exactly as bounded as it was before.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            MaxJailDiskBytes = 8L * 1024 * 1024
        });

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(expected: 8L * 1024 * 1024, create.MaxJailDiskBytes!.Value);
    }

    [Test]
    public async Task ExecuteAsync_WithDefaultOptions_AsksForTheDefaultComputeDiskCeiling()
    {
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(new ComputeOptions().MaxJailDiskBytes, create.MaxJailDiskBytes!.Value);
    }

    [Test]
    public async Task ExecuteAsync_WhenTheHostCannotIsolateTheFilesystem_RefusesBeforeProvisioningOrCreatingAJail()
    {
        // "Sandboxed" is what the tool's description promises the model and what the user approved the call on, so it
        // fails CLOSED — and it fails closed EARLY. The ordering is the assertion: a refusal that arrived after the
        // provision would have downloaded and unpacked a Python closure onto a node that can never run it, and after
        // the create it would have built a jail to explain itself from. Move the check below either of them and the
        // two null assertions here go red.
        var provider = new RecordingSandboxProvider(SandboxProviderCapabilities.SupportsNetworkPolicy
                                                    | SandboxProviderCapabilities.SupportsResourceLimits);
        var environment = new StubEnvironment("/provisioned/python");
        var identity = new StubIdentityProvider();
        var gateway = CreateGateway(provider, environment: environment, identityProvider: identity);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.Contains(rendered, "run_python rejected");
        AssertEx.Contains(rendered, "isolate");
        AssertEx.False(environment.Requested, "a host without the boundary must not provision an interpreter it can never run");
        AssertEx.False(identity.Requested, "nothing about the node's identity is needed to refuse");
        AssertEx.Null(provider.CreateRequest, "a host that cannot isolate the filesystem must not get as far as creating a jail");
        AssertEx.Empty(provider.KilledSandboxIds, "there is nothing to tear down when nothing was created");
    }

    [Test]
    public async Task ExecuteAsync_AsksForTheFilesystemBoundary_AndBindsOnlyTheTwoInterpreterTrees()
    {
        // The boundary is not a preference the provider may drop: a request naming it is rejected fail-closed by a
        // provider that cannot deliver it, which is what makes asking for it safe. The tree list is the other half —
        // naming the compute cache root instead of these two would hand the script the uv download cache, the uv
        // binary and the lockfile state marker along with the interpreter.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider,
            environment: new StubEnvironment("/provisioned/venv/bin/python", ["/provisioned/venv", "/provisioned/pythons"]));

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(SandboxIsolationMode.Filesystem, create.Isolation);
        var trees = AssertEx.NotNull(create.ReadOnlyTrees);
        AssertEx.Equal(expected: 2, trees.Count, "exactly the venv and the managed-CPython root it links into");
        AssertEx.Contains(trees, "/provisioned/venv");
        AssertEx.Contains(trees, "/provisioned/pythons");
        // No working directory is named: the sandbox's single writable tree IS the working directory.
        AssertEx.Null(AssertEx.NotNull(provider.CommandRequest).WorkingDirectory);
    }

    [Test]
    public async Task ExecuteAsync_WithDefaultOptions_PinsTheThreadCountBelowTheHostCoreCount()
    {
        // The libraries size their pools from the HOST's core count, which is not what the sandbox's CPU quota allows.
        // The default caps that at four rather than at the box's core count, so a 32-core host does not start 32 BLAS
        // threads against a two-core ceiling — and every one of them would also count against PidsLimit.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var threadLimit = AssertEx.NotNull(provider.CreateRequest).ThreadLimit!.Value;
        AssertEx.Equal(Math.Min(val1: 4, Environment.ProcessorCount), threadLimit);
        AssertEx.True(threadLimit <= 4, "the default must not scale with the host's core count");
    }

    [Test]
    public async Task ExecuteAsync_WhenOnlyTheCeilingsAreUnavailable_StillRuns()
    {
        // Resource ceilings bound COST, not reachability: degrading them is visible in the containment log and costs no
        // advertised guarantee, so they stay capability-gated where egress denial no longer is.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(SandboxNetworkPolicy.None, create.NetworkPolicy);
        AssertEx.Null(create.ResourceLimits);
    }

    [Test]
    public async Task ExecuteAsync_RendersExitCodeStdoutAndStderr()
    {
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 1, standardOutput: "4\n", standardError: "boom\n")
        };
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.Contains(rendered, "exit_code: 1");
        AssertEx.Contains(rendered, "stdout:\n4\n");
        AssertEx.Contains(rendered, "stderr:\nboom\n");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheScriptDidNotComplete_SaysSoRatherThanReportingAPlainExitCode()
    {
        // A timed-out run comes back Completed=false with exit code -1. Rendering that as a bare "exit_code: -1" would
        // read to a model as a normal failing program, and it would try to debug a script that never finished.
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = new SandboxCommandResult
            {
                ExecutionId = "x",
                ExitCode = -1,
                Completed = false
            }
        };
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            TimeoutSeconds = 7
        });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "while True: pass"
        });

        AssertEx.Contains(rendered, "did not finish within 7s");
        AssertEx.Contains(rendered, "terminated");
    }

    [Test]
    public async Task ExecuteAsync_WhenOutputExceedsTheCap_TruncatesWithTheSharedMarker()
    {
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 0, standardOutput: new string('a', 500), standardError: string.Empty)
        };
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            MaxOutputBytes = 100
        });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print('a' * 500)"
        });

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
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 0, standardOutput: "head", standardError: string.Empty) with
            {
                StandardOutputTruncated = true
            }
        };
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.Contains(rendered, "…[output truncated]");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheEnvironmentCannotBeProvisioned_ReturnsTheModelSafeReasonWithoutTouchingTheSandbox()
    {
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider,
            environment: new StubEnvironment(new ComputeEnvironmentException("The Python compute tool is available on Linux only.")));

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.Contains(rendered, "Linux only");
        AssertEx.Null(provider.CreateRequest, "a failed provision must not create a jail");
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_Throws()
    {
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            gateway.ExecuteAsync(new ComputeRunToolRequest
            {
                Code = "print(1)"
            }, cancellationTokenSource.Token));
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
        IComputePythonEnvironment? environment = null,
        IAgentHomeIdentityProvider? identityProvider = null)
    {
        return new ComputeToolGateway(provider,
            identityProvider ?? new StubIdentityProvider(),
            environment ?? new StubEnvironment("/provisioned/python"),
            Options.Create(options ?? new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            NullLogger<ComputeToolGateway>.Instance);
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        /// <summary>Whether the gateway got as far as needing an identity — the refusal-ordering test reads this.</summary>
        public bool Requested { get; private set; }

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            Requested = true;
            return Task.FromResult(new AgentHomeOwnerIdentity("owner-1", "node-1"));
        }
    }

    private sealed class StubEnvironment : IComputePythonEnvironment
    {
        private readonly ComputeEnvironmentException? _failure;
        private readonly ComputePythonRuntime _runtime;

        public StubEnvironment(string interpreter, IReadOnlyList<string>? readOnlyTrees = null)
        {
            _runtime = new ComputePythonRuntime(interpreter, readOnlyTrees ?? ["/provisioned/venv", "/provisioned/pythons"]);
        }

        public StubEnvironment(ComputeEnvironmentException failure)
        {
            _runtime = new ComputePythonRuntime(string.Empty, []);
            _failure = failure;
        }

        /// <summary>
        ///     Whether provisioning was ASKED for. It is what the refusal-ordering test asserts on, because the cost
        ///     the ordering exists to avoid — a venv download onto a node that can never run it — happens here.
        /// </summary>
        public bool Requested { get; private set; }

        public Task<ComputePythonRuntime> GetRuntimeAsync(CancellationToken cancellationToken = default)
        {
            Requested = true;

            return _failure is not null ? Task.FromException<ComputePythonRuntime>(_failure) : Task.FromResult(_runtime);
        }
    }

    /// <summary>
    ///     Records what the gateway asked for and answers with a canned result; no process is ever spawned. It DOES
    ///     keep a real directory per sandbox, because the jail is not incidental to what this suite checks: the
    ///     gateway now places the script's scratch inside it and relies on the jail teardown to reclaim it, so a fake
    ///     whose kill left nothing to observe could not tell a leaked scratch directory from a cleaned one.
    /// </summary>
    private sealed class RecordingSandboxProvider : IAgentSandboxRuntimeProvider
    {
        private readonly Dictionary<string, string> _jails = new(StringComparer.Ordinal);
        private readonly bool _namesAJailRoot;

        /// <param name="capabilities">What the gateway is told this provider can enforce.</param>
        /// <param name="namesAJailRoot">
        ///     False models a provider that reports no <see cref="SandboxHandle.WorkingRoot" /> — the deterministic
        ///     fake's shape, and the case the gateway must refuse rather than serve with unmetered scratch.
        /// </param>
        public RecordingSandboxProvider(SandboxProviderCapabilities capabilities, bool namesAJailRoot = true)
        {
            Capabilities = capabilities;
            _namesAJailRoot = namesAJailRoot;
        }

        public SandboxCreateRequest? CreateRequest { get; private set; }

        public SandboxCommandRequest? CommandRequest { get; private set; }

        public List<SandboxCreateRequest> CreateRequests { get; } = [];

        public List<SandboxCommandRequest> CommandRequests { get; } = [];

        public List<string> KilledSandboxIds { get; } = [];

        /// <summary>The jail directory handed to the most recent call, so a test can assert what sits under it.</summary>
        public string? LastJailRoot { get; private set; }

        /// <summary>Every sandbox-relative directory the gateway asked to be reset, in call order.</summary>
        public List<string> ResetDirectories { get; } = [];

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
            var sandboxId = "sandbox-" + CreateRequests.Count.ToString(CultureInfo.InvariantCulture);
            string? jail = null;
            if (_namesAJailRoot)
            {
                // Directly under the system temp root, not under a per-provider parent: KillAsync deletes the jail, so
                // nothing survives a passing test, and there is no leftover parent directory to sweep either.
                jail = Path.Combine(Path.GetTempPath(), "xe-compute-gateway-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(jail);
                _jails[sandboxId] = jail;
            }

            LastJailRoot = jail;

            return Task.FromResult(new SandboxHandle
            {
                ProviderName = ProviderName,
                SandboxId = sandboxId,
                AttachKey = request.AttachKey,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = request.AttachKey.ManifestVersion,
                WorkingRoot = jail
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
            return Task.FromResult(Result with
            {
                ExecutionId = request.ExecutionId
            });
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
            // The narrow contract the real providers serve: a known-empty directory under the jail, addressed by a
            // sandbox-relative path. Enough to prove the gateway asks for its scratch through the provider rather than
            // reaching around it to the host filesystem.
            ResetDirectories.Add(sandboxPath);
            var jail = _jails[handle.SandboxId];
            var resolved = Path.Combine(jail, sandboxPath.TrimStart('/'));
            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }

            Directory.CreateDirectory(resolved);
            return Task.CompletedTask;
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

            // Killing the jail is what discards everything below it — including the scratch the gateway no longer
            // deletes by hand.
            if (_jails.Remove(handle.SandboxId, out var jail) && Directory.Exists(jail))
            {
                Directory.Delete(jail, recursive: true);
            }

            return Task.CompletedTask;
        }
    }
}
