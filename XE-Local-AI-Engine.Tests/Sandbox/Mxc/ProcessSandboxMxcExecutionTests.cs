namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Text;
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

    private static ProcessSandboxRuntimeProvider Provider(IMxcSandboxRuntime? mxcRuntime)
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
            TimeProvider.System,
            logger: null,
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
