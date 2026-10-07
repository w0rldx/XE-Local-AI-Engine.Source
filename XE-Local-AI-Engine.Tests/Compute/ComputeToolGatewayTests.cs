namespace XE_Local_AI_Engine.Tests.Compute;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Gateway behavior against a recording sandbox provider: what it ASKS the sandbox for (its own runtime profile, a
///     filesystem boundary with exactly two read-only trees, an isolated interpreter invocation, and ceilings only
///     where the provider advertises them) and how it renders what comes back. The real containment is exercised live
///     in <see cref="ComputeSandboxLiveTests" />; this suite pins the request shape and the result vocabulary, which is
///     what a model actually reads.
/// </summary>
[Category(TestCategories.Unit)]
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
        AssertEx.Equal(SandboxIsolatedPaths.Posix.Home, environment["HOME"]);
        AssertEx.Equal(SandboxIsolatedPaths.Posix.Temp, environment["TMPDIR"]);
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
    public async Task ExecuteAsync_ComposesTheScratchEnvironmentFromTheHandle_NotFromAConstant()
    {
        // Under the Windows AppContainer boundary there is no mount namespace, so the provider reports the jail's HOST paths and the
        // script's HOME must be those — /work/home would name nothing on that host (ADR 0019).
        var hostJail = new SandboxIsolatedPaths
        {
            Work = "/host/jail",
            Home = "/host/jail/home",
            Temp = "/host/jail/.tmp"
        };
        var provider = new RecordingSandboxProvider(Contained)
        {
            IsolatedPaths = hostJail
        };

        _ = await CreateGateway(provider).ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var environment = AssertEx.NotNull(provider.CommandRequests[0].Environment);
        AssertEx.Equal(hostJail.Home, environment["HOME"]);
        AssertEx.Equal(hostJail.Temp, environment["TMPDIR"]);
        AssertEx.Equal(hostJail.Temp, environment["TMP"]);
        AssertEx.Equal(hostJail.Temp, environment["TEMP"]);
    }

    [Test]
    public async Task ExecuteAsync_WhenAnIsolatedHandleReportsNoPaths_FailsRatherThanGuessingAHome()
    {
        // A provider that claims Filesystem isolation without reporting its view is a bug; a guessed HOME could name a directory outside
        // the jail on the wrong platform, so the call fails loudly instead.
        var provider = new RecordingSandboxProvider(Contained)
        {
            IsolatedPaths = null
        };

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => CreateGateway(provider).ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        }));

        AssertEx.Empty(provider.CommandRequests, "nothing may run with an environment the provider did not report");
        AssertEx.Equal(expected: 1, provider.KilledSandboxIds.Count, "the jail is still torn down");
    }

    [Test]
    public void IsolatedPaths_OnWindows_PointTheProfileVariablesAtTheJailHome()
    {
        // The Windows half of the composition, asserted on every host: USERPROFILE/APPDATA/LOCALAPPDATA would otherwise lead a tool
        // to the real profile, which the AppContainer boundary denies, and the call would fail far from its cause.
        var paths = SandboxIsolatedPaths.ForHostJail(Path.Combine(Path.GetTempPath(), "jail"));

        var windows = paths.ToEnvironment(includeWindowsProfile: true);
        var posix = paths.ToEnvironment(includeWindowsProfile: false);

        foreach (var name in new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA" })
        {
            AssertEx.Equal(paths.Home, windows[name]);
            AssertEx.False(posix.ContainsKey(name), $"{name} is a Windows variable and is not set elsewhere");
        }

        AssertEx.Equal(Path.Combine(paths.Work, SandboxIsolatedPaths.HomeDirectoryName), paths.Home);
        AssertEx.Equal(Path.Combine(paths.Work, SandboxIsolatedPaths.TempDirectoryName), paths.Temp);
        AssertEx.Equal(new SandboxIsolatedPaths
        {
            Work = "/work",
            Home = "/work/home",
            Temp = "/tmp"
        }, SandboxIsolatedPaths.Posix,
            "the Linux view is unchanged by this round");
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
        // naming the compute cache root or the shared toolchain store instead of these two would hand the script the uv
        // cache, the uv binary and the lockfile state marker along with the interpreter.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider,
            environment: new StubEnvironment("/provisioned/compute-runtime/venv/.venv/bin/python", ["/provisioned/compute-runtime/venv/.venv", "/provisioned/python/pythons"]));

        _ = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        var create = AssertEx.NotNull(provider.CreateRequest);
        AssertEx.Equal(SandboxIsolationMode.Filesystem, create.Isolation);
        var trees = AssertEx.NotNull(create.ReadOnlyTrees);
        AssertEx.Equal(expected: 2, trees.Count, "exactly the venv and the managed-CPython root it links into");
        AssertEx.Contains(trees, "/provisioned/compute-runtime/venv/.venv");
        AssertEx.Contains(trees, "/provisioned/python/pythons");
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
            Result = Completed(exitCode: 0, standardOutput: new string('a', 5_000), standardError: string.Empty)
        };
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            MaxOutputBytes = ComputeOptions.MinOutputBytes
        });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print('a' * 5000)"
        });

        AssertEx.Contains(rendered, "…[output truncated]");
        AssertEx.False(rendered.Contains(new string('a', ComputeOptions.MinOutputBytes), StringComparison.Ordinal),
            "the capped stream must not carry more than the configured budget");
    }

    [Test]
    public async Task ExecuteAsync_WhenStdoutFillsTheCapAndTheScriptFails_KeepsTheTracebackTailWithinTheWholeBudget()
    {
        // Stdout at the cap pushed the rendering past the same-sized tool-result budget, which clipped the END —
        // the stderr section and its traceback.
        var stderr = "Traceback (most recent call last):\n" + new string('w', 50_000) + "\nValueError: boom\n";
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 1, standardOutput: new string('a', 70_000), standardError: stderr)
        };
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print('a' * 70000); raise ValueError('boom')"
        });

        AssertEx.True(Encoding.UTF8.GetByteCount(rendered) <= new ComputeOptions().MaxOutputBytes,
            "the whole rendering must fit the budget, or the tool-result pipeline clips its end");
        AssertEx.Contains(rendered, "exit_code: 1");
        AssertEx.Contains(rendered, "\nstderr:\n…[output truncated]\n");
        AssertEx.True(rendered.EndsWith("ValueError: boom\n", StringComparison.Ordinal), "the traceback's last line must survive");
        AssertEx.Contains(rendered, new string('a', 20_000), message: "stdout keeps the head it can fit");
    }

    [Test]
    public async Task ExecuteAsync_WhenATighterToolResultBudgetIsInScope_FitsThatBudgetInstead()
    {
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 1, standardOutput: new string('a', 10_000), standardError: new string('w', 10_000) + "KeyError: x\n")
        };
        var gateway = CreateGateway(provider);

        string rendered;
        using (ToolResultBudgetScope.BeginScope(2_000))
        {
            rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
            {
                Code = "raise KeyError('x')"
            });
        }

        AssertEx.True(rendered.Length <= 2_000, $"rendered {rendered.Length} chars");
        AssertEx.True(rendered.EndsWith("KeyError: x\n", StringComparison.Ordinal), "the traceback's last line must survive");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheToolPipelineBudgetIsBelowTheOutputCap_FitsThePipelineBudget()
    {
        // An operator-set pipeline budget below Compute:MaxOutputBytes would otherwise clip the tail again.
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 1, standardOutput: new string('a', 10_000), standardError: new string('w', 10_000) + "KeyError: x\n")
        };
        var gateway = CreateGateway(provider, pipelineOptions: new AgentToolPipelineOptions
        {
            MaxToolResultCharacters = 2_000
        });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "raise KeyError('x')"
        });

        AssertEx.True(rendered.Length <= 2_000, $"rendered {rendered.Length} chars");
        AssertEx.True(rendered.EndsWith("KeyError: x\n", StringComparison.Ordinal), "the traceback's last line must survive");
    }

    [Test]
    [Arguments(5, 5)]
    [Arguments(500, 30)]
    [Arguments(null, 30)]
    public async Task ExecuteDetailedAsync_ACallerTimeoutCanOnlyTightenTheNodeCeiling(int? requested, int expectedSeconds)
    {
        // A pythonTests criterion's timeout must bound the sandbox itself; it may never buy more than the node grants.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            TimeoutSeconds = 30
        });

        _ = await gateway.ExecuteDetailedAsync(new ComputeRunToolRequest
        {
            Code = "print(1)",
            TimeoutSeconds = requested
        }, requireResourceLimits: false);

        AssertEx.Equal(TimeSpan.FromSeconds(expectedSeconds), AssertEx.NotNull(provider.CommandRequest).Timeout);
    }

    [Test]
    public async Task ExecuteAsync_AtTheMinimumBudget_StaysWithinItAndKeepsTheHeadingsAndTheTracebackTail()
    {
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = new SandboxCommandResult
            {
                ExecutionId = "x",
                ExitCode = -1,
                Completed = false,
                StandardOutput = new string('a', 100_000),
                StandardError = new string('w', 100_000) + "\nRuntimeError: late\n"
            }
        };
        var gateway = CreateGateway(provider, new ComputeOptions
        {
            MaxOutputBytes = ComputeOptions.MinOutputBytes
        });

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print('a' * 100000)"
        });

        AssertEx.True(Encoding.UTF8.GetByteCount(rendered) <= ComputeOptions.MinOutputBytes, $"rendered {Encoding.UTF8.GetByteCount(rendered)} bytes");
        AssertEx.Contains(rendered, "did not finish within");
        AssertEx.Contains(rendered, "stdout:\n");
        AssertEx.Contains(rendered, "\nstderr:\n…[output truncated]\n");
        AssertEx.True(rendered.EndsWith("RuntimeError: late\n", StringComparison.Ordinal), "the traceback's last line must survive");
    }

    [Test]
    public async Task ExecuteAsync_WhenTheBudgetLeavesNoRoomForEitherStream_RendersOnlyTheHeadingsAndMarkers()
    {
        // A per-run scope can be seeded below the framing overhead; the result then says both streams were cut
        // rather than pretending either was empty.
        var provider = new RecordingSandboxProvider(Contained)
        {
            Result = Completed(exitCode: 1, standardOutput: "out", standardError: "err")
        };
        var gateway = CreateGateway(provider);

        string rendered;
        using (ToolResultBudgetScope.BeginScope(10))
        {
            rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
            {
                Code = "print(1)"
            });
        }

        AssertEx.Equal("exit_code: 1\nstdout:\n…[output truncated]\nstderr:\n…[output truncated]\n", rendered);
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
    public async Task ExecuteAsync_HoldsTheExecutionLeaseFromBeforeProvisioningUntilTheJailIsGone_AndReleasesItOnSuccess()
    {
        var environment = new StubEnvironment("/provisioned/python");
        var heldDuringExecute = 0;
        var provider = new RecordingSandboxProvider(Contained)
        {
            OnExecute = () => heldDuringExecute = environment.LeasesHeld
        };

        _ = await CreateGateway(provider, environment: environment).ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.True(environment.LeaseHeldAtGetRuntime, "the lease is taken before the runtime, or a remove can slip between them");
        AssertEx.Equal(expected: 1, heldDuringExecute);
        AssertEx.Equal(expected: 0, environment.LeasesHeld);
    }

    [Test]
    public async Task ExecuteAsync_ReleasesTheExecutionLease_WhenTheSandboxFails()
    {
        var environment = new StubEnvironment("/provisioned/python");
        var provider = new RecordingSandboxProvider(Contained)
        {
            OnExecute = static () => throw new IOException("sandbox failed")
        };

        _ = await AssertEx.ThrowsAsync<IOException>(() => CreateGateway(provider, environment: environment).ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        }));

        AssertEx.Equal(expected: 0, environment.LeasesHeld);
    }

    [Test]
    public async Task ExecuteAsync_ReleasesTheExecutionLease_WhenCancelledMidRun()
    {
        var environment = new StubEnvironment("/provisioned/python");
        using var cancellation = new CancellationTokenSource();
        var provider = new RecordingSandboxProvider(Contained)
        {
            OnExecute = () =>
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        };

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => CreateGateway(provider, environment: environment).ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        }, cancellation.Token));

        AssertEx.Equal(expected: 0, environment.LeasesHeld);
    }

    [Test]
    public async Task ExecuteAsync_ReleasesTheExecutionLease_WhenProvisioningIsRefused()
    {
        var environment = new StubEnvironment(new ComputeEnvironmentException("The pinned compute runtime could not be provisioned on this node."));

        _ = await CreateGateway(new RecordingSandboxProvider(Contained), environment: environment).ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.Equal(expected: 0, environment.LeasesHeld);
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

    [Test]
    public async Task ExecuteDetailedAsync_WhenComputeDisabled_RefusesWithoutProvisioningOrCreatingAJail()
    {
        // The kill-switch moved here from RunPythonToolHandler, so this is now the ONLY place it is read — and it has
        // to refuse before the interpreter is provisioned, exactly as the handler's short-circuit did.
        var provider = new RecordingSandboxProvider(Contained);
        var environment = new StubEnvironment("/provisioned/python");
        var gateway = new ComputeToolGateway(provider,
            new StubIdentityProvider(),
            environment,
            Options.Create(new ComputeOptions()),
            Options.Create(new LocalContainerOptions()),
            Options.Create(new AgentToolPipelineOptions()),
            SeededNodeRuntimeSettings.FromSeed("Compute:Enabled", value: false),
            NullLogger<ComputeToolGateway>.Instance);

        var outcome = await gateway.ExecuteDetailedAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        }, requireResourceLimits: false);

        AssertEx.False(outcome.Ran, "a disabled node must not execute anything");
        AssertEx.Equal(ComputeRefusalCodes.ComputeDisabled, outcome.RefusalCode);
        AssertEx.Contains(AssertEx.NotNull(outcome.RefusalMessage), "Compute:Enabled=false");
        AssertEx.False(environment.Requested, "a disabled node must not provision an interpreter");
        AssertEx.Null(provider.CreateRequest, "and must not create a jail");
    }

    [Test]
    public async Task ExecuteDetailedAsync_WhenTheStoredSwitchIsOff_RefusesOverAnOnSeed()
    {
        var provider = new RecordingSandboxProvider(Contained);
        var environment = new StubEnvironment("/provisioned/python");
        var gateway = CreateGateway(provider, environment: environment, runtimeSettings: SeededNodeRuntimeSettings.Create(new Dictionary<string, string?>
        {
            ["Compute:Enabled"] = "true"
        }, static () => new StoredNodeSettings
        {
            ComputeEnabled = false
        }));

        var outcome = await gateway.ExecuteDetailedAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        }, requireResourceLimits: false);

        AssertEx.Equal(ComputeRefusalCodes.ComputeDisabled, outcome.RefusalCode);
        AssertEx.False(environment.Requested, "a stored off must refuse before provisioning, whatever the seed says");
    }

    [Test]
    public async Task ExecuteDetailedAsync_ValidatesTheRequest_EvenWhenCalledDirectly()
    {
        // The bypass that motivated moving the checks: a caller reaching the gateway straight past the handler used to
        // get no validation at all. Both bounds are asserted here because both used to live in the handler.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        var blank = await gateway.ExecuteDetailedAsync(new ComputeRunToolRequest
        {
            Code = "   "
        }, requireResourceLimits: false);
        var oversized = await gateway.ExecuteDetailedAsync(new ComputeRunToolRequest
        {
            Code = new string('x', ComputeToolDefinition.CodeMaxLength + 1)
        }, requireResourceLimits: false);

        AssertEx.Equal(ComputeRefusalCodes.InvalidRequest, blank.RefusalCode);
        AssertEx.Contains(AssertEx.NotNull(blank.RefusalMessage), "invalid", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(ComputeRefusalCodes.InvalidRequest, oversized.RefusalCode);
        AssertEx.Null(provider.CreateRequest, "an invalid request must not create a jail");
    }

    [Test]
    public async Task ExecuteDetailedAsync_WhenCeilingsAreUnenforceableAndRequired_RefusesWithNoResourceLimits()
    {
        // SandboxResourceCeilings.Resolve returns null on a backend without the capability, so the script would run
        // unbounded in CPU, memory and process count. A caller executing operator-authored code unattended says
        // requireResourceLimits, and is refused rather than run.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        var outcome = await gateway.ExecuteDetailedAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        }, requireResourceLimits: true);

        AssertEx.False(outcome.Ran);
        AssertEx.Equal(ComputeRefusalCodes.NoResourceLimits, outcome.RefusalCode);
        AssertEx.Null(provider.CreateRequest, "the refusal must land before a jail is created");
    }

    [Test]
    public async Task RunPython_WithoutResourceLimits_StillRuns_BehaviourUnchanged()
    {
        // THE no-regression test for the asymmetry the operator kept: run_python asks for no ceilings, so a host that
        // cannot impose them keeps running the tool exactly as it does today. Inverting this assertion is what flipping
        // that decision would look like.
        var provider = new RecordingSandboxProvider(Contained);
        var gateway = CreateGateway(provider);

        var rendered = await gateway.ExecuteAsync(new ComputeRunToolRequest
        {
            Code = "print(1)"
        });

        AssertEx.Contains(rendered, "exit_code: 0");
        AssertEx.NotNull(provider.CommandRequest);
        AssertEx.Null(AssertEx.NotNull(provider.CreateRequest).ResourceLimits,
            "a backend without the capability gets no ceilings — and still runs the tool");
    }

    [Test]
    public async Task ExecuteDetailedAsync_IsTheOnlyExecutionPathThatReadsComputeEnabled()
    {
        // An architecture test, because the failure it guards is a SECOND copy of the flag appearing on an execution
        // path and then drifting — which is the whole finding this boundary answers. Asserted against the source
        // rather than against behaviour: a duplicate read is invisible at runtime until the two disagree.
        //
        // The allow-list is exactly two entries and both are stated. Anything else appearing here is a decision, not a
        // tidy-up: it means some other code decided for itself whether this node may execute Python.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // The boundary itself — the ONE execution gate.
            Path.Combine("Services", "Compute", "Implementation", "ComputeToolGateway.cs"),
            // Not an execution gate: it decides whether the seeded mathematician persona is OFFERED run_python at all.
            // A persona seeded with the tool on a disabled node would still be refused by the gateway; this read only
            // keeps a dead tool out of the persona.
            Path.Combine("Services", "Agents", "Implementation", "MathematicianAgentSeeder.cs"),
            // Not a reader at all: it takes ComputeOptions for the shared sandbox CEILING defaults, and its own
            // `.Enabled` is AgentHomeOptions'. Listed because the scan below is per file rather than per expression.
            Path.Combine("Services", "AgentHome", "Implementation", "AgentHomeService.cs"),
            // Not an execution gate: the Managed Python status reports a disabled node as Unsupported and refuses to
            // repair or remove its venv. Nothing it does runs a script; the gateway still decides that.
            Path.Combine("Services", "ManagedPython", "ManagedPythonStatusService.cs")
        };
        var application = Path.Combine(RepositoryPaths.Root, "XE-Local-AI-Engine.Client.Application");
        var readers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(application, "*.cs", SearchOption.AllDirectories))
        {
            // The declaration itself, the options validator and the accessor that resolves the switch are not reads of the node's answer.
            if (Path.GetFileName(file) is "ComputeOptions.cs" or "ComputeOptionsValidator.cs" or "INodeRuntimeSettings.cs" or "NodeRuntimeSettings.cs")
            {
                continue;
            }

            // Comment lines are dropped first: several files legitimately DESCRIBE the kill-switch, and a test that
            // counted prose would be satisfied by deleting a sentence rather than by deleting a read. What is left is
            // a per-FILE co-occurrence rather than a per-expression one, which over-reports (see the third allow-list
            // entry) — deliberately, because under-reporting is the failure that matters here.
            var code = (await File.ReadAllLinesAsync(file))
                       .Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                       .ToArray();
            // A reader goes through the accessor; a co-occurring options read is the stale second copy this guards against.
            if (code.Any(static line => line.Contains("GetComputeEnabled", StringComparison.Ordinal))
                || (code.Any(static line => line.Contains("omputeOptions", StringComparison.Ordinal))
                    && code.Any(static line => line.Contains(".Enabled", StringComparison.Ordinal))))
            {
                readers.Add(Path.GetRelativePath(application, file));
            }
        }

        AssertEx.True(readers.Contains(Path.Combine("Services", "Compute", "Implementation", "ComputeToolGateway.cs")),
            "the gateway must read the kill-switch — it is the boundary");
        AssertEx.Equal(expected: 0,
            readers.Count(reader => !allowed.Contains(reader)),
            $"only the compute gateway may gate execution on the kill-switch; unexpected readers: {string.Join(", ", readers.Where(reader => !allowed.Contains(reader)))}");
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
        IAgentHomeIdentityProvider? identityProvider = null,
        AgentToolPipelineOptions? pipelineOptions = null,
        INodeRuntimeSettings? runtimeSettings = null)
    {
        options ??= new ComputeOptions();
        // Every test in this suite is about a node that has OPTED IN, through the switch's configuration seed; the switch
        // itself is asserted by its own tests. Without it every call here would refuse before reaching the sandbox.
        return new ComputeToolGateway(provider,
            identityProvider ?? new StubIdentityProvider(),
            environment ?? new StubEnvironment("/provisioned/python"),
            Options.Create(options),
            Options.Create(new LocalContainerOptions()),
            Options.Create(pipelineOptions ?? new AgentToolPipelineOptions()),
            runtimeSettings ?? SeededNodeRuntimeSettings.FromSeed("Compute:Enabled", value: true),
            NullLogger<ComputeToolGateway>.Instance);
    }

    private sealed class StubIdentityProvider : IAgentHomeIdentityProvider
    {
        /// <summary>Whether the gateway got as far as needing an identity — the refusal-ordering test reads this.</summary>
        public bool Requested { get; private set; }

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            Requested = true;
            return Task.FromResult(new AgentHomeOwnerIdentity
            {
                OwnerUserId = "owner-1",
                NodeId = "node-1"
            });
        }
    }

    private sealed class StubEnvironment : IComputePythonEnvironment
    {
        private readonly ComputeEnvironmentException? _failure;
        private readonly ComputePythonRuntime _runtime;
        private int _leasesHeld;

        public StubEnvironment(string interpreter, IReadOnlyList<string>? readOnlyTrees = null)
        {
            _runtime = new ComputePythonRuntime
            {
                InterpreterPath = interpreter,
                ReadOnlyTrees = readOnlyTrees ?? ["/provisioned/compute-runtime/venv/.venv", "/provisioned/python/pythons"]
            };
        }

        public StubEnvironment(ComputeEnvironmentException failure)
        {
            _runtime = new ComputePythonRuntime
            {
                InterpreterPath = string.Empty,
                ReadOnlyTrees = []
            };
            _failure = failure;
        }

        /// <summary>
        ///     Whether provisioning was ASKED for. It is what the refusal-ordering test asserts on, because the cost
        ///     the ordering exists to avoid — a venv download onto a node that can never run it — happens here.
        /// </summary>
        public bool Requested { get; private set; }

        /// <summary>Execution leases currently held.</summary>
        public int LeasesHeld => Volatile.Read(ref _leasesHeld);

        /// <summary>Whether a lease was already held when the runtime was asked for, which is the order a remove relies on.</summary>
        public bool LeaseHeldAtGetRuntime { get; private set; }

        public IDisposable AcquireExecutionLease()
        {
            _ = Interlocked.Increment(ref _leasesHeld);
            return new Release(this);
        }

        public void ReleaseLease()
        {
            _ = Interlocked.Decrement(ref _leasesHeld);
        }

        public Task<ComputePythonRuntime> GetRuntimeAsync(CancellationToken cancellationToken = default)
        {
            Requested = true;
            LeaseHeldAtGetRuntime = LeasesHeld > 0;

            return _failure is not null ? Task.FromException<ComputePythonRuntime>(_failure) : Task.FromResult(_runtime);
        }
    }

    /// <summary>Hands a <see cref="StubEnvironment" /> lease back exactly once.</summary>
    private sealed class Release : IDisposable
    {
        private StubEnvironment? _owner;

        public Release(StubEnvironment owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner)
            {
                owner.ReleaseLease();
            }
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

        /// <summary>Runs while the script "executes": a test reads state there, or throws to fail or cancel the call.</summary>
        public Action? OnExecute { get; init; }

        /// <summary>The view this fake reports for an isolated sandbox; a test swaps in a Windows-shaped one.</summary>
        public SandboxIsolatedPaths? IsolatedPaths { get; init; } = SandboxIsolatedPaths.Posix;

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
                WorkingRoot = jail,
                Isolation = request.Isolation,
                IsolatedPaths = request.Isolation == SandboxIsolationMode.Filesystem ? IsolatedPaths : null
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
            OnExecute?.Invoke();
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
