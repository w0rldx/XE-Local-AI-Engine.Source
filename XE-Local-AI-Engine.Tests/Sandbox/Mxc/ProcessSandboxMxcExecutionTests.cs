namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ProcessSandboxRuntimeProvider" /> under the AppContainer boundary through a substituted
///     <see cref="IMxcSandboxRuntime" />: the spawned policy, captured output, and MXC's kill on every abnormal end.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ProcessSandboxMxcExecutionTests
{
    [Test]
    public async Task Execute_SpawnsTheLockedPolicy_AndReturnsTheExitCodeAndCapturedOutput()
    {
        var child = Child(stdout: "hello\nworld\n");
        child.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 7
        });
        var runtime = Runtime(child);
        using var provider = Provider(runtime);
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        var result = await provider.ExecuteAsync(handle, Command("python.exe", "-c", "print(1)"));

        AssertEx.True(result.Completed);
        AssertEx.Equal(7, result.ExitCode);
        AssertEx.Equal("hello\nworld\n", result.StandardOutput);
        await runtime.Received(1).SpawnAsync(Arg.Is<ContainerRequest>(request =>
                request.Command == "python.exe -c print(1)"
                && request.Containment is Containment.ProcessContainer
                && request.Filesystem!.ReadwritePaths.Single() == handle.WorkingRoot
                && request.Environment!["USERPROFILE"] == handle.IsolatedPaths!.Home),
            Arg.Any<CancellationToken>());
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Execute_WhenMxcReportsItsOwnTimeout_ReturnsTheTimedOutShape_AfterKilling()
    {
        var child = Child();
        child.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 0,
            TimedOut = true
        }, new WaitResult
        {
            ExitCode = 1
        });
        using var provider = Provider(Runtime(child));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        var result = await provider.ExecuteAsync(handle, Command("cmd.exe") with
        {
            Timeout = TimeSpan.FromMinutes(5)
        });

        AssertEx.False(result.Completed);
        AssertEx.Equal(-1, result.ExitCode);
        AssertEx.Equal("Command timed out.", result.StandardError);
        child.Received().Kill();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Execute_WhenTheCommandIsCancelled_KillsTheContainedTree()
    {
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = BlockingChild();
        var runtime = Runtime(child, spawned);
        using var provider = Provider(runtime);
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        var execution = provider.ExecuteAsync(handle, Command("cmd.exe"));
        await spawned.Task;
        await provider.CancelCommandAsync(handle, "mxc-command");
        var result = await execution;

        AssertEx.False(result.Completed);
        AssertEx.Equal("Command was cancelled before completion.", result.StandardError);
        child.Received().Kill();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Execute_WhenTheCallerCancels_KillsAndPropagates()
    {
        var spawned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = BlockingChild();
        using var provider = Provider(Runtime(child, spawned));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());
        using var caller = new CancellationTokenSource();

        var execution = provider.ExecuteAsync(handle, Command("cmd.exe"), caller.Token);
        await spawned.Task;
        await caller.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => execution);
        child.Received().Kill();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Execute_WhenTheChildStopsReadingStdin_CancellationKillsIt_AndEndsTheWrite()
    {
        // The SDK's stdin write is synchronous and ignores the token, so only a kill (closing the pipe) can end it.
        using var stdin = new StalledStdin();
        var child = BlockingChild();
        child.StandardInput.Returns(stdin);
        child.When(process => process.Kill()).Do(_ => stdin.Release());
        using var provider = Provider(Runtime(child));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());
        using var caller = new CancellationTokenSource();

        // Far larger than a pipe buffer; Task.Run because an unfixed write blocks the calling thread itself.
        var execution = Task.Run(() => provider.ExecuteAsync(handle, Command("python.exe") with
        {
            StandardInput = new string('x', 1 << 20)
        }, caller.Token));
        await stdin.Writing;
        await caller.CancelAsync();

        // A TimeoutException here means the cancelled command stayed stuck in a stdin write the child never reads.
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => execution.WaitAsync(TestBudgets.Contended));
        child.Received().Kill();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Create_UnderTheAppContainerBoundary_WithExplicitLimits_IsRefusedNamingTheBoundary()
    {
        using var provider = Provider(Runtime(Child()));

        var refusal = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() =>
            provider.CreateOrAttachAsync(IsolatedRequest() with
            {
                ResourceLimits = new SandboxResourceLimits
                {
                    MemoryMb = 512
                }
            }));

        AssertEx.Contains(refusal.Message, "mxc-processcontainer AppContainer boundary");
        AssertEx.Contains(refusal.Message, "enforces no CPU, memory or process ceiling; omit SandboxResourceLimits for a timeout-only run");
    }

    [Test]
    public async Task Create_UnderTheAppContainerBoundary_WithoutLimits_IsServed()
    {
        using var provider = Provider(Runtime(Child()));

        var handle = await provider.CreateOrAttachAsync(IsolatedRequest() with
        {
            ResourceLimits = null
        });

        AssertEx.NotNull(handle.IsolatedPaths);
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Capabilities_UnderTheAppContainerBoundary_GiveRunPythonAndMcpStdioNoCeilings()
    {
        // run_python and sandboxed MCP stdio derive their create request's limits from these capabilities, so the boundary never refuses them.
        using var provider = Provider(Runtime(Child()));

        AssertEx.False(provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits));
        AssertEx.Null(SandboxResourceCeilings.Resolve(SandboxWorkloads.RunPython, provider.Capabilities, new ComputeOptions(), new LocalContainerOptions()));
        AssertEx.Null(SandboxResourceCeilings.Resolve(SandboxWorkloads.McpStdio, provider.Capabilities, new ComputeOptions(), new LocalContainerOptions()));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Execute_WhenTheSandboxIsKilledWhileTheSpawnIsPending_KillsTheChild_AndReportsNotCompleted()
    {
        // The kill sweeps the in-flight set while the spawn is awaited, so a child registered afterwards would run where no teardown looks.
        var spawning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = BlockingChild();
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.SpawnAsync(Arg.Any<ContainerRequest>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            spawning.TrySetResult();
            await release.Task;
            return child;
        });
        using var provider = Provider(runtime);
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        var execution = provider.ExecuteAsync(handle, Command("cmd.exe"));
        await spawning.Task;
        await provider.KillAsync(handle);
        release.SetResult();

        await AssertEx.CompletesAsync(execution, TestBudgets.Contended, "a child started into a killed sandbox must not be left running");
        var result = await execution;
        AssertEx.False(result.Completed);
        AssertEx.Equal("The sandbox was killed before the command started.", result.StandardError);
        child.Received().Kill();
        child.Received(1).Dispose();
    }

    [Test]
    public async Task Execute_WhenMxcRefusesTheSpawn_ReturnsANotLaunchedResultNamingTheBoundary()
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.SpawnAsync(Arg.Any<ContainerRequest>(), Arg.Any<CancellationToken>())
               .ThrowsAsync(new SandboxCapabilityNotSupportedException("policy refused"));
        using var provider = Provider(runtime);
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        var result = await provider.ExecuteAsync(handle, Command("cmd.exe"));

        AssertEx.False(result.Completed);
        AssertEx.Equal(-1, result.ExitCode);
        AssertEx.Contains(result.StandardError, "AppContainer boundary (policy refused)");
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Execute_WithNoMxcRuntimeInTheProcess_FailsClosed()
    {
        // Off Windows the provider has no runtime of its own, so a boundary-served sandbox must refuse rather than run a plain child.
        Skip.When(OperatingSystem.IsWindows(), "On Windows the provider builds the real MXC runtime when none is injected.");
        using var provider = Provider(mxcRuntime: null);
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        var result = await provider.ExecuteAsync(handle, Command("cmd.exe"));

        AssertEx.False(result.Completed);
        AssertEx.Contains(result.StandardError, "the MXC runtime is not available");
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Interactive_ExposesRawStdout_PumpsStderr_AndDisposeKills()
    {
        var child = Child(stdout: "{\"jsonrpc\":\"2.0\"}\n", stderr: "server log\n");
        child.StandardInput.Returns(new MemoryStream());
        var exit = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        child.WaitAsync(Arg.Any<CancellationToken>()).Returns(call => exit.Task.WaitAsync(call.Arg<CancellationToken>()));
        child.When(process => process.Kill()).Do(_ => exit.TrySetResult(new WaitResult
        {
            ExitCode = 1
        }));
        using var provider = Provider(Runtime(child));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        var interactive = await provider.StartInteractiveAsync(handle, Command("server.exe"));
        using (var reader = new StreamReader(interactive.StandardOutput, Encoding.UTF8, leaveOpen: true))
        {
            AssertEx.Equal("{\"jsonrpc\":\"2.0\"}", await reader.ReadLineAsync());
        }

        await interactive.DisposeAsync();

        child.Received().Kill();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Interactive_WhenTheServerStopsReadingStdin_ACancelledWriteKillsIt_AndReturns()
    {
        // The MCP transport writes through this stream; the SDK's write would ignore the transport's token and block forever.
        using var stdin = new StalledStdin();
        var child = BlockingChild();
        child.StandardInput.Returns(stdin);
        child.When(process => process.Kill()).Do(_ => stdin.Release());
        using var provider = Provider(Runtime(child));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());
        var interactive = await provider.StartInteractiveAsync(handle, Command("server.exe"));
        using var caller = new CancellationTokenSource();

        var write = Task.Run(async () => await interactive.StandardInput.WriteAsync(new byte[]
        {
            1,
            2,
            3
        }, caller.Token));
        await stdin.Writing;
        await caller.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => write.WaitAsync(TestBudgets.Contended));
        child.Received().Kill();
        await interactive.DisposeAsync();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Interactive_WhenAReaderCancelsAnIdleStdoutRead_TheReadReturns_AndDisposeIsNotHeld()
    {
        // The MCP transport cancels its read loop and awaits it before killing the child; the SDK's read would wait for the server to write.
        using var stdout = new IdleStdout();
        var closer = Substitute.For<IMxcStreamCloser>();
        closer.When(c => c.Close()).Do(_ => stdout.Release());
        var child = BlockingChild();
        child.StandardOutput.Returns(stdout);
        child.StandardOutputCloser.Returns(closer);
        using var provider = Provider(Runtime(child));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());
        var interactive = await provider.StartInteractiveAsync(handle, Command("server.exe"));
        using var reader = new CancellationTokenSource();

        var read = Task.Run(async () => await interactive.StandardOutput.ReadAsync(new byte[64], reader.Token));
        await stdout.Reading;
        await reader.CancelAsync();

        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TestBudgets.Contended));
        closer.Received(1).Close();
        await AssertEx.CompletesAsync(interactive.DisposeAsync().AsTask(), TestBudgets.Contended, "disposal must not wait on the abandoned read");
        child.Received().Kill();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Interactive_StdinWrites_PassTheBytesThrough()
    {
        using var stdin = new MemoryStream();
        var child = BlockingChild();
        child.StandardInput.Returns(stdin);
        using var provider = Provider(Runtime(child));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());
        var interactive = await provider.StartInteractiveAsync(handle, Command("server.exe"));

        await interactive.StandardInput.WriteAsync("{}\n"u8.ToArray());
        await interactive.StandardInput.FlushAsync();

        AssertEx.Equal("{}\n", Encoding.UTF8.GetString(stdin.ToArray()));
        child.DidNotReceive().Kill();
        await interactive.DisposeAsync();
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Execute_WithAWorkingDirectoryEqualToAGrantedTree_StartsTheChildInThatTree()
    {
        var tree = Directory.CreateTempSubdirectory("xe-mxc-tree-").FullName;
        try
        {
            var runtime = Runtime(Child());
            using var provider = Provider(runtime);
            var handle = await provider.CreateOrAttachAsync(IsolatedRequest() with
            {
                ReadOnlyTrees = [tree]
            });

            // The trailing separator proves the match is made after canonicalization, as the transport's own form may differ.
            _ = await provider.ExecuteAsync(handle, Command("node.exe", "dist/index.js") with
            {
                WorkingDirectory = tree + Path.DirectorySeparatorChar
            });

            await runtime.Received(1).SpawnAsync(Arg.Is<ContainerRequest>(request => request.WorkingDirectory == tree), Arg.Any<CancellationToken>());
            await provider.KillAsync(handle);
        }
        finally
        {
            Directory.Delete(tree, recursive: true);
        }
    }

    [Test]
    public async Task Execute_WithAWorkingDirectoryOutsideTheJailThatIsNoGrantedTree_IsRefused()
    {
        var tree = Directory.CreateTempSubdirectory("xe-mxc-tree-").FullName;
        var sibling = Directory.CreateTempSubdirectory("xe-mxc-sibling-").FullName;
        try
        {
            var runtime = Runtime(Child());
            using var provider = Provider(runtime);
            var handle = await provider.CreateOrAttachAsync(IsolatedRequest() with
            {
                ReadOnlyTrees = [tree]
            });

            // A granted tree opens exactly itself: a path that leaves the jail for anywhere else is still the jail guard's refusal.
            _ = await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => provider.ExecuteAsync(handle, Command("node.exe") with
            {
                WorkingDirectory = Path.GetRelativePath(AssertEx.NotNull(handle.WorkingRoot), sibling)
            }));
            await runtime.DidNotReceive().SpawnAsync(Arg.Any<ContainerRequest>(), Arg.Any<CancellationToken>());
            await provider.KillAsync(handle);
        }
        finally
        {
            Directory.Delete(tree, recursive: true);
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Test]
    public async Task Execute_WhenMxcReportsWarningsForTheSpawn_LogsTheCount_AndTheTextsOnlyRedacted()
    {
        var child = Child();
        child.Warnings.Returns(["read-only grant for G:\\tools skipped", "spawned node.exe --token abcdef0123456789"]);
        child.WaitAsync(Arg.Any<CancellationToken>()).Returns(new WaitResult
        {
            ExitCode = 0
        });
        var logger = new RecordingLogger<ProcessSandboxRuntimeProvider>();
        using var provider = Provider(Runtime(child), logger);
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());

        _ = await provider.ExecuteAsync(handle, Command("node.exe", "--token", "abcdef0123456789"));

        AssertEx.Contains(logger.AllText, "reported 2 warning(s) for the sandbox launch of node.exe");
        AssertEx.Contains(logger.AllText, "read-only grant for G:\\tools skipped | spawned node.exe --token [REDACTED]");
        // MXC may echo the command line: an argument secret reaches no log line, at Warning or at Debug.
        AssertEx.False(logger.AllText.Contains("abcdef0123456789", StringComparison.Ordinal), $"the argument secret leaked into the log:\n{logger.AllText}");
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task Dispose_WhileAKilledServerIsStillExiting_WaitsForTheExit_ThenRemovesTheJail()
    {
        // MXC's kill returns before the tree is gone, and on Windows a jail deleted under a dying MCP server (its cwd) survives emptied.
        // This child exits only once someone waits for it after the kill, so a teardown that does not wait deletes while it still runs.
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = false;
        string? jail = null;
        var child = Child();
        child.StandardInput.Returns(new MemoryStream());
        child.When(process => process.Kill()).Do(_ => killed = true);
        child.WaitAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (Volatile.Read(ref killed))
            {
                exited.TrySetResult(Directory.Exists(jail));
                exit.TrySetResult(new WaitResult
                {
                    ExitCode = -1
                });
            }

            return exit.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        var provider = Provider(Runtime(child));
        var handle = await provider.CreateOrAttachAsync(IsolatedRequest());
        jail = handle.WorkingRoot;
        var interactive = await provider.StartInteractiveAsync(handle, Command("server.exe"));

        provider.Dispose();

        AssertEx.True(exited.Task.IsCompleted, "the teardown must wait for the killed server to exit before it deletes the jail");
        AssertEx.True(await exited.Task, "the jail must still exist while the killed server is exiting");
        AssertEx.False(Directory.Exists(jail), "the jail must be gone once the server has exited");
        await interactive.DisposeAsync();
    }

    [Test]
    public async Task Dispose_WhenWaitingForAKilledServerThrows_StillRemovesEveryJail()
    {
        // A server disposed under the teardown's wait (its own scope ended concurrently) must not strand its jail or the next one.
        var runtime = Runtime([
            KilledServer(_ => throw new ObjectDisposedException(nameof(IMxcProcess))),
            KilledServer(_ => throw new ObjectDisposedException(nameof(IMxcProcess)))
        ]);
        var provider = Provider(runtime);
        var jails = await StartServersAsync(provider, count: 2);

        provider.Dispose();

        foreach (var jail in jails)
        {
            AssertEx.False(Directory.Exists(jail), $"the jail {jail} must be removed although waiting for its server threw");
        }
    }

    [Test]
    public async Task Dispose_WithThreeServersThatNeverExit_WaitsOneSharedBudgetForAllOfThem()
    {
        var never = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ExitBudgetTimeProvider();
        var runtime = Runtime([
            KilledServer(token => never.Task.WaitAsync(token)),
            KilledServer(token => never.Task.WaitAsync(token)),
            KilledServer(token => never.Task.WaitAsync(token))
        ]);
        using var provider = Provider(runtime, timeProvider: time); // Dispose is idempotent; the test drives the first call itself
        var jails = await StartServersAsync(provider, count: 3);
        time.CountFromHere();

        var dispose = Task.Run(provider.Dispose);
        var budget = await time.FirstBudget.WaitAsync(TestBudgets.Contended);
        budget.Fire();

        // A per-jail budget would arm a second timer here that nothing fires, and the shutdown would hang on it.
        await AssertEx.CompletesAsync(dispose, TestBudgets.Contended, "terminate-all must be bounded by one budget for every jail");
        AssertEx.Equal(1, time.BudgetsArmed);
        foreach (var jail in jails)
        {
            AssertEx.False(Directory.Exists(jail), $"the jail {jail} must be removed once the shared budget ran out");
        }

        never.SetResult(new WaitResult
        {
            ExitCode = -1
        });
    }

    private static async Task<List<string>> StartServersAsync(ProcessSandboxRuntimeProvider provider, int count)
    {
        var jails = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var handle = await provider.CreateOrAttachAsync(IsolatedRequest());
            jails.Add(AssertEx.NotNull(handle.WorkingRoot));
            _ = await provider.StartInteractiveAsync(handle, Command("server.exe"));
        }

        return jails;
    }

    // Keeps running after Kill() until a wait decides its fate, as a contained tree that MXC is still tearing down.
    private static IMxcProcess KilledServer(Func<CancellationToken, Task<WaitResult>> waitAfterKill)
    {
        var running = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = 0;
        var child = Child();
        child.StandardInput.Returns(new MemoryStream());
        child.When(process => process.Kill()).Do(_ => Volatile.Write(ref killed, 1));
        child.WaitAsync(Arg.Any<CancellationToken>()).Returns(call => Volatile.Read(ref killed) == 1
            ? waitAfterKill(call.Arg<CancellationToken>())
            : running.Task.WaitAsync(call.Arg<CancellationToken>()));
        return child;
    }

    private static IMxcSandboxRuntime Runtime(IMxcProcess[] children)
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.SpawnAsync(Arg.Any<ContainerRequest>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(children[0]), [.. children.Skip(1).Select(Task.FromResult)]);
        return runtime;
    }

    private static ProcessSandboxRuntimeProvider Provider(IMxcSandboxRuntime? mxcRuntime,
        ILogger<ProcessSandboxRuntimeProvider>? logger = null,
        TimeProvider? timeProvider = null)
    {
        var containment = SandboxContainment.None with
        {
            FilesystemIsolationUnavailableReason = "the host is Windows",
            AppContainerBoundary = new SandboxAppContainerBoundary
            {
                Mechanism = "mxc-processcontainer",
                Tier = "AppContainerDacl",
                Maturity = SandboxMechanismMaturity.Preview
            }
        };
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            timeProvider ?? TimeProvider.System,
            logger,
            new SandboxLauncher(new StubProbe(containment), deniedRootCandidates: []),
            previewPolicy: new PreviewsOn(),
            mxcRuntime: mxcRuntime);
    }

    private static IMxcSandboxRuntime Runtime(IMxcProcess child, TaskCompletionSource? spawned = null)
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.SpawnAsync(Arg.Any<ContainerRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            spawned?.TrySetResult();
            return Task.FromResult(child);
        });
        return runtime;
    }

    private static IMxcProcess Child(string stdout = "", string stderr = "")
    {
        var child = Substitute.For<IMxcProcess>();
        child.Id.Returns(4242u);
        child.StandardOutput.Returns(new MemoryStream(Encoding.UTF8.GetBytes(stdout)));
        child.StandardError.Returns(new MemoryStream(Encoding.UTF8.GetBytes(stderr)));
        child.Warnings.Returns([]);
        child.TryGetExitCode(out Arg.Any<int>()).Returns(false);
        return child;
    }

    // Runs until killed, as a contained command does: MXC's cancelled wait leaves it running, only Kill() ends it.
    private static IMxcProcess BlockingChild()
    {
        var child = Child();
        var exit = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        child.WaitAsync(Arg.Any<CancellationToken>()).Returns(call => exit.Task.WaitAsync(call.Arg<CancellationToken>()));
        child.When(process => process.Kill()).Do(_ => exit.TrySetResult(new WaitResult
        {
            ExitCode = -1
        }));
        return child;
    }

    private static SandboxCommandRequest Command(string executable, params string[] arguments) =>
        new()
        {
            ExecutionId = "mxc-command",
            Executable = executable,
            Arguments = arguments
        };

    private static SandboxCreateRequest IsolatedRequest() =>
        new()
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "compute-" + Guid.NewGuid().ToString("N"),
                ManifestVersion = 1
            },
            RuntimeProfile = "compute",
            NetworkPolicy = SandboxNetworkPolicy.None,
            Isolation = SandboxIsolationMode.Filesystem
        };

    /// <summary>Hands out a manually fired timer for every teardown exit budget and the real one for everything else.</summary>
    private sealed class ExitBudgetTimeProvider : TimeProvider
    {
        private int _budgetsArmed;
        private TaskCompletionSource<ManualTimer> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ManualTimer> FirstBudget => Volatile.Read(ref _first).Task;

        public int BudgetsArmed => Volatile.Read(ref _budgetsArmed);

        /// <summary>Forgets the budgets armed so far (setup's), so the counts describe what follows.</summary>
        public void CountFromHere()
        {
            Volatile.Write(ref _first, new TaskCompletionSource<ManualTimer>(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _budgetsArmed, 0);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != SandboxLifecycleRegistry.InFlightExitBudget)
            {
                return System.CreateTimer(callback, state, dueTime, period);
            }

            var timer = new ManualTimer(callback, state);
            Interlocked.Increment(ref _budgetsArmed);
            Volatile.Read(ref _first).TrySetResult(timer);
            return timer;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        public void Fire() =>
            _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }

    private sealed class PreviewsOn : IExecutionPreviewPolicy
    {
        public bool PreviewMechanismsEnabled => true;
    }

    private sealed class StubProbe : ISandboxContainmentProbe
    {
        public StubProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }
}
