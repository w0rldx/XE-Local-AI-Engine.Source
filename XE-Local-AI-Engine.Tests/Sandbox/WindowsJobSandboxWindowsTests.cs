namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>The plain-path Job Object ceilings on real Windows (ADR 0025): probe, stdio, membership, each ceiling, teardown and the WMI escape.</summary>
/// <remarks>
///     Never skips on Windows: the <c>windows-sandbox</c> job fails on a skip, and a host whose probe fails here is a red, not a pass. A
///     WMI-created process is OUTSIDE the job (ADR 0025: a governor, not a boundary). The CPU test changes this process's affinity, so
///     the class does not run in parallel.
/// </remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows10.0.19041.0")]
[NotInParallel]
public sealed class WindowsJobSandboxWindowsTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    [Test]
    public async Task TheProbe_ProvesTheCeilings_OnThisHost()
    {
        var containment = new HostSandboxContainmentProbe().Containment;

        AssertEx.True(containment.SupportsResourceLimits, containment.ResourceLimitsUnavailableReason);
        AssertEx.True(containment.WindowsJobProcessorCount >= Environment.ProcessorCount, $"processor count {containment.WindowsJobProcessorCount}");
        AssertEx.Null(containment.SystemdRunPath);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ACommand_RoundTripsStdio_AndItsExitCode_InsideItsJob()
    {
        using var provider = Provider();
        var handle = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { MemoryMb = 2048, PidsLimit = 64, CpuCount = 2 }));
        try
        {
            var echo = await provider.ExecuteAsync(handle, Cmd("echo", "more") with { StandardInput = "hello\r\n" });
            var exit = await provider.ExecuteAsync(handle, Cmd("exit", "exit", "3"));

            AssertEx.Equal(0, echo.ExitCode, echo.StandardError);
            AssertEx.Contains(echo.StandardOutput, "hello");
            AssertEx.Equal(3, exit.ExitCode);
        }
        finally
        {
            await provider.KillAsync(handle);
        }
    }

    [Test]
    public async Task AChild_AndAGrandchild_AreMembersOfTheCommandsJob()
    {
        // cmd is the command, powershell its child, ping its grandchild; powershell prints its own pid and ping's.
        using var process = Launch("cmd /d /c powershell -NoProfile -NonInteractive -Command \"$g = Start-Process ping -ArgumentList '-n','60','127.0.0.1' -WindowStyle Hidden -PassThru; Write-Output ($PID.ToString() + ' ' + $g.Id); Start-Sleep 60\"",
            WindowsJobLimits.Map(4096, 64, null, Environment.ProcessorCount));
        try
        {
            var pids = (await ReadLineAsync(process.StandardOutput)).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            AssertEx.Equal(2, pids.Length);
            foreach (var pid in pids)
            {
                AssertEx.True(IsMember(int.Parse(pid, CultureInfo.InvariantCulture), process.Job), $"process {pid} is not a member of the command's job");
            }
        }
        finally
        {
            process.Terminate();
        }
    }

    [Test]
    public async Task TheCommitCeiling_FailsAnAllocationPastIt_AndAdmitsOneUnderIt()
    {
        // Stop, so a refused allocation ends the script: a method exception alone only ends its statement, and 'allocated' would still print.
        const string allocate = "$ErrorActionPreference = 'Stop'; 'starting'; $b = [byte[]]::new(800MB); $b[0] = 1; 'allocated'";
        using var provider = Provider();
        var tight = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { MemoryMb = 512 }));
        var roomy = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { MemoryMb = 2048 }));
        try
        {
            var past = await provider.ExecuteAsync(tight, PowerShell("commit-past", allocate));
            var under = await provider.ExecuteAsync(roomy, PowerShell("commit-under", allocate));

            // The marker proves powershell started, so the missing "allocated" is the commit ceiling's doing, not a failed start.
            AssertEx.Contains(past.StandardOutput, "starting");
            AssertEx.False(past.StandardOutput.Contains("allocated", StringComparison.Ordinal), "an 800 MB array was allocated inside a 512 MB job");
            AssertEx.Contains(under.StandardOutput, "starting");
            AssertEx.Contains(under.StandardOutput, "allocated");
        }
        finally
        {
            await provider.KillAsync(tight);
            await provider.KillAsync(roomy);
        }
    }

    [Test]
    public async Task TheProcessCeiling_StopsAChainPastIt_AndAdmitsTheSameChainUnderAWiderOne()
    {
        string[] chainOfFive = ["cmd", "/d", "/c", "cmd", "/d", "/c", "cmd", "/d", "/c", "cmd", "/d", "/c", "exit", "7"];
        using var provider = Provider();
        var tight = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { PidsLimit = 4 }));
        var wide = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { PidsLimit = 8 }));
        try
        {
            var past = await provider.ExecuteAsync(tight, Cmd("chain-5-tight", chainOfFive));
            var under = await provider.ExecuteAsync(wide, Cmd("chain-5-wide", chainOfFive));

            // The control: the same chain runs to its exit code under a wider cap, so the tight cap's other exit is the ceiling's doing.
            AssertEx.Equal(7, under.ExitCode, under.StandardError);
            AssertEx.True(past.Completed, $"the command must run and exit, not fail to launch or time out: {past.StandardError}");
            AssertEx.NotEqual(7, past.ExitCode, "a chain of five processes ran inside a four-process job");
        }
        finally
        {
            await provider.KillAsync(tight);
            await provider.KillAsync(wide);
        }
    }

    /// <summary>One core's worth under a two-processor affinity: the rate is against the WHOLE system, not the processors a member may use.</summary>
    /// <remarks>
    ///     <c>start /affinity 3</c> pins powershell from creation, so this test host never runs under a changed affinity (ADR 0025, CPU
    ///     rule). The same spinners in a job without a CPU rate must first use more than 1.5 cores, or the runner cannot show the cap. The
    ///     denominator is read in this unrestricted host, where the system and affinity counts agree, so this test cannot catch
    ///     <c>WindowsJobApi.GetSystemProcessorCount</c> turning affinity-relative.
    /// </remarks>
    [Test]
    public async Task TheCpuCeiling_HoldsOneCore_UnderRestrictedAffinity()
    {
        var system = new WindowsJobLauncher().SystemProcessorCount;
        AssertEx.True(system >= 2, $"this test needs at least two logical processors; the host has {system}");

        var uncapped = await SpinnerCoresAsync(WindowsJobLimits.Map(4096, 64, null, system));
        AssertEx.True(uncapped > 1.5, $"without a CPU rate the spinners used only {uncapped:F2} cores, so this runner cannot show a one-core ceiling: a test-environment failure, not a pass");

        var cores = await SpinnerCoresAsync(WindowsJobLimits.Map(4096, 64, 1, system));
        AssertEx.True(cores > 0.3, $"the spinners used {cores:F2} cores; the measurement saw no work");
        AssertEx.True(cores <= 1.35, $"the job used {cores:F2} cores under a one-core ceiling");
    }

    /// <summary>Raw UTF-8 bytes a tool writes come back as the same text: the job path decodes UTF-8, as the MXC path does (ADR 0025).</summary>
    [Test]
    public async Task Utf8Output_RoundTripsThroughTheJobPath()
    {
        // The bytes, not the console code page: g r ü ß e in UTF-8, written straight to the stdout handle as git would write them.
        const string script = "$o = [Console]::OpenStandardOutput(); $b = [byte[]](0x67,0x72,0xC3,0xBC,0xC3,0x9F,0x65,0x0A); $o.Write($b, 0, $b.Length); $o.Flush()";
        using var provider = Provider();
        var handle = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { MemoryMb = 2048 }));
        try
        {
            var result = await provider.ExecuteAsync(handle, PowerShell("utf8", script));

            AssertEx.Equal(0, result.ExitCode, result.StandardError);
            AssertEx.Equal("grüße", result.StandardOutput.TrimEnd('\r', '\n'));
        }
        finally
        {
            await provider.KillAsync(handle);
        }
    }

    /// <summary>
    ///     A process outside the job holding stdout open does not hold the command: the held read is cancelled, the unterminated line the
    ///     root printed is still delivered and marks stdout incomplete, and both pumps finish.
    /// </summary>
    /// <remarks>
    ///     The root duplicates its own stdout into this test host, which is outside the job, then exits. Windows documents cancelling a
    ///     blocked read on another thread for overlapped I/O, so the engine reads stdout and stderr overlapped (ADR 0025). No elapsed-time
    ///     bound: a wall clock here also counts PowerShell's startup and <c>Add-Type</c>. The exact drain interval is proven on a manual clock
    ///     by <c>WindowsJobSandboxChildProcessTests</c>.
    /// </remarks>
    [Test]
    public async Task AStdoutHeldOutsideTheJob_IsCancelled_DeliversThePendingLine_AndBothPumpsFinish()
    {
        var script = """
                     Add-Type -Name K -Namespace XeTest -MemberDefinition '[DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int n); [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid); [DllImport("kernel32.dll", SetLastError = true)] public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, bool inherit, uint options); [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();'
                     $engine = [XeTest.K]::OpenProcess(0x40, $false, ENGINE_PID)
                     $held = [IntPtr]::Zero
                     if (-not [XeTest.K]::DuplicateHandle([XeTest.K]::GetCurrentProcess(), [XeTest.K]::GetStdHandle(-11), $engine, [ref]$held, 0, $false, 2)) { exit 9 }
                     [Console]::Error.WriteLine($held.ToInt64())
                     [Console]::Out.Write('partial')
                     [Console]::Out.Flush()
                     """.Replace("ENGINE_PID", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var errors = new List<string>();
        var output = new List<string>();
        using var child = new WindowsJobSandboxChildProcess(Launch($"powershell -NoProfile -NonInteractive -EncodedCommand {encoded}", WindowsJobLimits.Map(4096, 256, null, Environment.ProcessorCount)),
            TimeProvider.System,
            line =>
            {
                lock (output)
                {
                    output.Add(line);
                }
            },
            line =>
            {
                lock (errors)
                {
                    errors.Add(line);
                }
            });
        try
        {
            // Bounded from outside, so a drain that never ends fails here instead of hanging the run.
            var wait = child.WaitForExitAsync(CancellationToken.None);
            await AssertEx.CompletesAsync(wait, MxcChildProcess.PumpDrainBudget + TimeSpan.FromSeconds(60), "the wait never returned: the held read was not cancelled");
            var exitCode = await wait;

            AssertEx.Equal(0, exitCode, $"the root could not hand its stdout over: {string.Join(" | ", errors)}");
            AssertEx.True(HeldHandle() != IntPtr.Zero, "the root reported no duplicated handle, so nothing held stdout");
            lock (output)
            {
                AssertEx.Equal("partial", string.Join('|', output), "the unterminated line is delivered before the wait returns");
            }

            AssertEx.True(child.StandardOutputIncomplete, "a line cut off by the drain budget makes stdout incomplete");
            using var pumps = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            AssertEx.True(await child.TryWaitForExitAsync(pumps.Token), "a pump stayed blocked on the held pipe after its read was cancelled");
        }
        finally
        {
            // Read here too, so a failed wait still closes the duplicated handle.
            var held = HeldHandle();
            if (held != IntPtr.Zero)
            {
                _ = CloseHandle(held);
            }
        }

        IntPtr HeldHandle()
        {
            lock (errors)
            {
                return errors.Select(static line => long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? checked((IntPtr)value) : IntPtr.Zero)
                             .FirstOrDefault(static value => value != IntPtr.Zero);
            }
        }
    }

    [Test]
    public async Task AProcessTheCommandLeftRunning_DiesWithTheCommand()
    {
        using var provider = Provider();
        var handle = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { MemoryMb = 4096 }));
        try
        {
            var result = await provider.ExecuteAsync(handle,
                PowerShell("orphan", "(Start-Process ping -ArgumentList '-n','120','127.0.0.1' -WindowStyle Hidden -PassThru).Id"));
            var orphan = int.Parse(result.StandardOutput.Trim(), CultureInfo.InvariantCulture);

            await AssertEx.EventuallyAsync(() => HasExited(orphan), Settle, $"process {orphan} outlived the command that started it");
        }
        finally
        {
            await provider.KillAsync(handle);
        }
    }

    [Test]
    public async Task AFailedMembershipCheck_LeavesNoProcess()
    {
        // The child really lands outside the command's job, in a decoy job with no kill-on-close, so only a process-level terminate
        // reaches it: the job's terminate and its close both miss it.
        using var api = new OutsideTheJobApi();

        var failure = AssertEx.Throws<InvalidOperationException>(() =>
            new WindowsJobLauncher(api).Start(StartInfo("cmd /d /c ping -n 60 127.0.0.1"), WindowsJobLimits.Map(4096, 64, null, Environment.ProcessorCount)));

        AssertEx.Contains(failure.Message, "not a member");
        await AssertEx.EventuallyAsync(() => HasExited(api.CreatedProcessId), Settle, "the suspended child survived a failed membership check");
    }

    [Test]
    public async Task ACommandThatCannotBeCreated_IsALaunchFailure()
    {
        using var provider = Provider();
        var handle = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { MemoryMb = 2048 }));
        try
        {
            var result = await provider.ExecuteAsync(handle, new SandboxCommandRequest
            {
                ExecutionId = "windows-job-missing",
                Executable = @"C:\xe-definitely-missing\tool.exe",
                Arguments = [],
                Timeout = TimeSpan.FromSeconds(30)
            });

            AssertEx.True(result.LaunchFailed);
            AssertEx.Contains(result.StandardError, "inside its Windows job");
        }
        finally
        {
            await provider.KillAsync(handle);
        }
    }

    [Test]
    public async Task TheEnginesPipeEnds_AreNeverInheritable_AndTheJobAllowsNoBreakaway()
    {
        using var process = Launch("cmd /d /q /k", WindowsJobLimits.Map(2048, 64, 1, Environment.ProcessorCount));
        try
        {
            foreach (var stream in new[] { process.StandardInput, process.StandardOutput, process.StandardError })
            {
                var handle = ((FileStream)stream).SafeFileHandle;
                AssertEx.True(GetHandleInformation(handle, out var flags), $"GetHandleInformation failed ({Marshal.GetLastPInvokeError()})");
                AssertEx.Equal(0u, flags & HandleFlagInherit, "an engine-side pipe end is inheritable");
            }

            var limits = WindowsJobNative.QueryInformation<WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(process.Job,
                WindowsJobNative.JobObjectExtendedLimitInformation);
            AssertEx.Equal(0u, limits.BasicLimitInformation.LimitFlags & (WindowsJobLimits.LimitBreakawayOk | WindowsJobLimits.LimitSilentBreakawayOk));
            AssertEx.Equal(WindowsJobLimits.LimitKillOnJobClose, limits.BasicLimitInformation.LimitFlags & WindowsJobLimits.LimitKillOnJobClose);
        }
        finally
        {
            process.Terminate();
        }

        await Task.CompletedTask;
    }

    /// <summary>Documents the escape ADR 0025 records: a WMI-created process is not a member, so no ceiling or kill reaches it.</summary>
    /// <remarks>The job is a governor, not a boundary; if this ever starts failing, revisit the threat model, not this test.</remarks>
    [Test]
    public async Task AWmiCreatedProcess_IsOutsideTheJob()
    {
        using var process = Launch("powershell -NoProfile -NonInteractive -Command \"(Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = 'ping -n 60 127.0.0.1' }).ProcessId\"",
            WindowsJobLimits.Map(4096, 64, null, Environment.ProcessorCount));
        var escaped = 0;
        try
        {
            escaped = int.Parse((await ReadLineAsync(process.StandardOutput)).Trim(), CultureInfo.InvariantCulture);
            AssertEx.True(escaped > 0, "WMI did not report a created process");

            AssertEx.False(IsMember(escaped, process.Job), "the WMI-created process is a member; the documented escape changed");
        }
        finally
        {
            process.Terminate();
            if (escaped > 0)
            {
                try
                {
                    using var stray = Process.GetProcessById(escaped);
                    stray.Kill();
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
            }
        }
    }

    /// <summary>
    ///     The negative control for the handle guarantee: a <c>Process.Start</c> inside the launch window inherits the test's own inheritable
    ///     handle but none of the command's three pipe ends (ADR 0025, the handle parent).
    /// </summary>
    /// <remarks>
    ///     The control pipe proves the bystander really inherited what was inheritable; its break after the bystander's exit ties it to that
    ///     process. A sandbox end the bystander held would keep the engine's stdout or stderr read from breaking, or stdin writable.
    /// </remarks>
    [Test]
    public async Task AConcurrentProcessStart_InheritsNoSandboxPipeEnd()
    {
        var owned = new List<IDisposable>();
        SafeFileHandle? controlRead = null, controlWrite = null;
        Process? bystander = null;
        try
        {
            using var process = StartInsideTheHandleWindow("cmd /d /c exit 0",
                () =>
                {
                    AssertEx.True(WindowsJobNative.CreatePipe(out controlRead, out controlWrite, IntPtr.Zero, 0), $"control pipe ({Marshal.GetLastPInvokeError()})");
                    AssertEx.True(SetHandleInformation(controlWrite, HandleFlagInherit, HandleFlagInherit), $"control inherit flag ({Marshal.GetLastPInvokeError()})");
                    bystander = Process.Start(new ProcessStartInfo("ping", "-n 600 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true });
                    controlWrite.Dispose();
                },
                owned);

            var control = AssertEx.NotNull(controlRead);
            AssertEx.Equal(0, PeekError(control), "control: the bystander did not inherit the test's inheritable handle, so it proves nothing");
            await AssertEx.CompletesAsync(process.WaitForExitAsync(CancellationToken.None), TimeSpan.FromSeconds(30), "the command did not exit");
            await AssertEx.EventuallyAsync(() => ActiveMembers(process.Job) == 0, Settle, "the command's job still has members");

            AssertEx.Equal(ErrorBrokenPipe, PeekError(((FileStream)process.StandardOutput).SafeFileHandle), "stdout: another process still holds the command's stdout write end");
            AssertEx.Equal(ErrorBrokenPipe, PeekError(((FileStream)process.StandardError).SafeFileHandle), "stderr: another process still holds the command's stderr write end");
            var written = WriteFile(((FileStream)process.StandardInput).SafeFileHandle, [0x41], 1, out _, IntPtr.Zero);
            var stdinError = written ? 0 : Marshal.GetLastPInvokeError();
            AssertEx.True(stdinError is ErrorNoData or ErrorBrokenPipe, $"stdin: a one-byte write returned {stdinError}, so another process still holds the command's stdin read end");

            var started = AssertEx.NotNull(bystander);
            started.Kill();
            await AssertEx.CompletesAsync(started.WaitForExitAsync(), TimeSpan.FromSeconds(30), "the bystander did not exit");
            // The bystander's console host inherits its handles and exits just after it, so the break is awaited, bounded.
            await AssertEx.EventuallyAsync(() => PeekError(control) == ErrorBrokenPipe, Settle, "the control pipe outlived the bystander, so the bystander was not what held it");
        }
        finally
        {
            if (bystander is { HasExited: false })
            {
                bystander.Kill();
            }

            bystander?.Dispose();
            controlRead?.Dispose();
            controlWrite?.Dispose();
            DisposeAll(owned);
        }
    }

    /// <summary>The command gets exactly its three streams, through helper-local values the forwarder made differ from every engine value.</summary>
    [Test]
    public async Task TheChild_GetsExactlyItsThreeStreams_FromTheHandleParent()
    {
        using var native = new RecordingLaunchNative { AvoidEngineValues = true };
        using var parent = new WindowsJobHandleParent(native);
        var baseline = -1;
        var sentinelReads = new List<SafeFileHandle>();
        var sentinel = IntPtr.Zero;
        native.Before = call =>
        {
            if (call == "Park#1")
            {
                baseline = HandleCount(native.Helpers[^1]);
            }
            else if (call == "CreateCommand#1")
            {
                // Inheritable in the helper but not in the list: the command must not get it.
                AssertEx.True(WindowsJobNative.CreatePipe(out var sentinelRead, out var sentinelWrite, IntPtr.Zero, 0), "sentinel pipe");
                sentinelReads.Add(sentinelRead);
                AssertEx.True(WindowsJobNative.DuplicateHandle(WindowsJobNative.GetCurrentProcess(), sentinelWrite, AssertEx.NotNull(native.HelperArgument), out sentinel, 0, true, WindowsJobNative.DuplicateSameAccess),
                    $"sentinel duplicate ({Marshal.GetLastPInvokeError()})");
                sentinelWrite.Dispose();
            }
        };
        var marker = "IN-" + Guid.NewGuid().ToString("N");
        using var process = new WindowsJobLauncher(new WindowsJobApi(parent)).Start(StartInfo("cmd /d /v:on /c \"set /p L=&echo OUT:!L!&1>&2 echo ERR:!L!\""),
            WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount));
        try
        {
            native.ReleaseReservations();
            AssertEx.True(WindowsJobNative.DuplicateHandle(native.Helpers[^1], sentinel, IntPtr.Zero, IntPtr.Zero, 0, false, WindowsJobNative.DuplicateCloseSource), "sentinel close");

            AssertEx.Equal(3, native.ParkedValues.Count);
            AssertEx.Equal(string.Join(',', native.ParkedValues), string.Join(',', native.Listed.Single()), "the handle list and hStd* carry the three final parked values, in order");
            for (var index = 0; index < 3; index++)
            {
                AssertEx.NotEqual(native.EngineEnds[index], native.ParkedValues[index], "a parked value equals its engine value, so the test cannot tell them apart");
            }

            AssertEx.False(WaitSignalled(native.Commands[0], TimeSpan.Zero), "the command must still be waiting on stdin");
            AssertEx.Equal(0u, Available(((FileStream)process.StandardOutput).SafeFileHandle), "the command wrote before it was given input");
            AssertEx.Equal(ErrorBrokenPipe, PeekError(sentinelReads.Single()), "the command inherited a helper handle outside its list");

            await process.StandardInput.WriteAsync(System.Text.Encoding.ASCII.GetBytes(marker + "\r\n"));
            await process.StandardInput.FlushAsync();
            await process.StandardInput.DisposeAsync();
            using var answer = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stdout = new StreamReader(process.StandardOutput);
            using var stderr = new StreamReader(process.StandardError);
            var output = stdout.ReadToEndAsync(answer.Token);
            var error = stderr.ReadToEndAsync(answer.Token);
            await AssertEx.CompletesAsync(Task.WhenAll(output, error), TimeSpan.FromSeconds(30), "stdout/stderr did not answer");
            await AssertEx.CompletesAsync(process.WaitForExitAsync(CancellationToken.None), TimeSpan.FromSeconds(30), "the command did not exit");

            AssertEx.Equal("OUT:" + marker, (await output).TrimEnd('\r', '\n', ' '));
            AssertEx.Equal("ERR:" + marker, (await error).TrimEnd('\r', '\n', ' '));
            AssertEx.Equal(0, process.ExitCode);
            AssertEx.Equal(baseline, HandleCount(native.Helpers[^1]), "the helper keeps a handle the launch put there");
        }
        finally
        {
            process.Terminate();
            foreach (var sentinelRead in sentinelReads)
            {
                sentinelRead.Dispose();
            }
        }
    }

    [Test]
    public async Task TheJobHierarchy_PutsTheHelperInTheParentJob_AndTheCommandInBoth()
    {
        using var native = new RecordingLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        using var process = new WindowsJobLauncher(new WindowsJobApi(parent)).Start(StartInfo("cmd /d /q /k"), WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount));
        try
        {
            var parentJob = AssertEx.NotNull(native.ParentJob);
            AssertEx.True(InJob(native.Helpers[0], parentJob), "the helper is not in the parent job");
            AssertEx.False(InJob(native.Helpers[0], process.Job), "the helper is in a command's job");
            AssertEx.True(InJob(native.Commands[0], process.Job), "the command is not in its job");
            AssertEx.True(InJob(native.Commands[0], parentJob), "the command is not under the parent job");
            foreach (var handle in new[] { AssertEx.NotNull(native.HelperArgument), AssertEx.NotNull(native.ParentJobArgument) })
            {
                AssertEx.True(GetHandleInformation(handle, out var flags), $"GetHandleInformation failed ({Marshal.GetLastPInvokeError()})");
                AssertEx.Equal(0u, flags & HandleFlagInherit, "a handle-parent handle is inheritable in the engine");
            }
        }
        finally
        {
            process.Terminate();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task KillingOneCommand_AndReplacingTheHelper_LeavesTheOtherUsable()
    {
        using var native = new RecordingLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var launcher = new WindowsJobLauncher(new WindowsJobApi(parent));
        var limits = WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount);
        using var killed = launcher.Start(StartInfo("cmd /d /q /k"), limits);
        using var survivor = launcher.Start(StartInfo("cmd /d /v:on /c \"set /p L=&echo OUT:!L!&exit 5\""), limits);
        try
        {
            killed.Terminate();
            await AssertEx.CompletesAsync(killed.WaitForExitAsync(CancellationToken.None), Settle, "the killed command did not exit");
            AssertEx.True(WindowsJobNative.TerminateProcess(native.Helpers[0], 1), "terminating the helper");
            AssertEx.True(WaitSignalled(native.Helpers[0], Settle), "the helper did not exit");

            await survivor.StandardInput.WriteAsync("x\r\n"u8.ToArray());
            await survivor.StandardInput.DisposeAsync();
            AssertEx.Equal("OUT:x", (await ReadLineAsync(survivor.StandardOutput)).Trim());
            await AssertEx.CompletesAsync(survivor.WaitForExitAsync(CancellationToken.None), Settle, "the survivor did not exit");
            AssertEx.Equal(5, survivor.ExitCode);

            using var next = launcher.Start(StartInfo("cmd /d /c exit 0"), limits);
            await AssertEx.CompletesAsync(next.WaitForExitAsync(CancellationToken.None), Settle, "the next launch did not run");
            AssertEx.Equal(2, native.Helpers.Count, "a new helper replaces the dead one");
            AssertEx.False(WaitSignalled(native.Helpers[1], TimeSpan.Zero), "the new helper is alive");
        }
        finally
        {
            killed.Terminate();
            survivor.Terminate();
        }
    }

    [Test]
    public async Task ClosingTheParentJob_EndsTheHelperAndEveryCommand()
    {
        using var native = new RecordingLaunchNative();
        var parent = new WindowsJobHandleParent(native);
        var launcher = new WindowsJobLauncher(new WindowsJobApi(parent));
        var limits = WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount);
        var first = launcher.Start(StartInfo("cmd /d /q /k"), limits);
        var second = launcher.Start(StartInfo("cmd /d /q /k"), limits);
        try
        {
            AssertEx.False(WaitSignalled(native.Commands[0], TimeSpan.Zero) || WaitSignalled(native.Commands[1], TimeSpan.Zero), "both commands are alive before the close");

            // Only the parent job closes: both wrappers and their kill-on-close jobs stay open and unterminated.
            parent.Dispose();
            native.CloseParentJobCopy();

            AssertEx.True(WaitSignalled(native.Helpers[0], TimeSpan.FromSeconds(10)), "the helper outlived the parent job");
            AssertEx.True(WaitSignalled(native.Commands[0], TimeSpan.FromSeconds(10)), "the first command outlived the parent job");
            AssertEx.True(WaitSignalled(native.Commands[1], TimeSpan.FromSeconds(10)), "the second command outlived the parent job");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task AHelperThatDiedBetweenLaunches_IsReplaced()
    {
        using var native = new RecordingLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var launcher = new WindowsJobLauncher(new WindowsJobApi(parent));
        var limits = WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount);
        using (var first = launcher.Start(StartInfo("cmd /d /c exit 0"), limits))
        {
            await AssertEx.CompletesAsync(first.WaitForExitAsync(CancellationToken.None), Settle, "the first command did not exit");
        }

        AssertEx.True(WindowsJobNative.TerminateProcess(native.Helpers[0], 1), "terminating the helper");
        AssertEx.True(WaitSignalled(native.Helpers[0], Settle), "the helper did not exit");
        using var second = launcher.Start(StartInfo("cmd /d /c exit 4"), limits);
        await AssertEx.CompletesAsync(second.WaitForExitAsync(CancellationToken.None), Settle, "the second command did not exit");

        AssertEx.Equal(4, second.ExitCode);
        AssertEx.Equal(2, native.Helpers.Count);
        AssertEx.Equal(1, native.ParentJobsCreated, "the replacement lives in the same parent job");
    }

    [Test]
    public async Task AHelperThatDiesInsideTheLaunchWindow_RefusesTheLaunch_AndTheNextRuns()
    {
        using var native = new RecordingLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        native.Before = call =>
        {
            if (call == "Park#2")
            {
                AssertEx.True(WindowsJobNative.TerminateProcess(native.Helpers[0], 1), "terminating the helper");
                AssertEx.True(WaitSignalled(native.Helpers[0], Settle), "the helper did not exit");
            }
        };
        var launcher = new WindowsJobLauncher(new WindowsJobApi(parent));
        var limits = WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount);
        var marker = Path.Combine(Path.GetTempPath(), "xe-helper-died-" + Guid.NewGuid().ToString("N"));
        try
        {
            var refused = AssertEx.Throws<InvalidOperationException>(() => launcher.Start(StartInfo($"cmd /d /c echo x>\"{marker}\""), limits));
            AssertEx.Contains(refused.Message, "the handle parent");
            AssertEx.False(File.Exists(marker), "the command ran although its helper died inside the launch");

            using var next = launcher.Start(StartInfo("cmd /d /c exit 0"), limits);
            await AssertEx.CompletesAsync(next.WaitForExitAsync(CancellationToken.None), Settle, "the next launch did not run");
            AssertEx.Equal(2, native.Helpers.Count);
        }
        finally
        {
            File.Delete(marker);
        }
    }

    /// <summary>A failure at each step of a launch leaves no process, and no launch proceeds while an ended process has not exited.</summary>
    /// <remarks>
    ///     Each case drives <c>WindowsJobApi.CreateSuspended</c> (or the handle parent) directly with a test-owned kill-on-close job and the
    ///     parent job held open and unterminated until after the exit assertions, so only the launch's own kill can end a command.
    /// </remarks>
    [Test]
    [Arguments("helper-verification")]
    [Arguments("partial-park")]
    [Arguments("missing-executable")]
    [Arguments("post-create")]
    [Arguments("unconfirmed-exit")]
    [Arguments("child-construction")]
    [Arguments("membership-kill")]
    public async Task AFailureAtEachStep_LeavesNoProcess_AndNoNewLaunchBeforeExitIsConfirmed(string step)
    {
        using var native = new RecordingLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var api = new WindowsJobApi(parent);
        using var job = api.CreateJob();
        api.SetExtendedLimits(job, WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount));
        var baseline = -1;
        var marker = Path.Combine(Path.GetTempPath(), "xe-step-" + Guid.NewGuid().ToString("N"));
        var command = StartInfo("cmd /d /q /k");
        native.Before = call =>
        {
            if (call == "Park#1")
            {
                baseline = HandleCount(native.Helpers[^1]);
            }

            var fails = step switch
            {
                "helper-verification" => call == "IsProcessInJob#1",
                "partial-park" => call == "Park#2",
                "post-create" or "unconfirmed-exit" => call == "Unpark#1",
                _ => false
            };
            if (fails)
            {
                throw new System.ComponentModel.Win32Exception(31, "injected by the test");
            }
        };
        try
        {
            switch (step)
            {
                case "helper-verification":
                    _ = AssertEx.Throws<InvalidOperationException>(() => api.CreateSuspended(job, command));
                    AssertEx.True(WaitSignalled(native.Helpers[0], TimeSpan.FromSeconds(5)), "the unverified helper did not exit");
                    api.CreateSuspended(job, command).Dispose();
                    AssertEx.Equal(2, native.Helpers.Count);
                    AssertEx.Equal(1, native.ParentJobsCreated, "the next helper lives in the same parent job");
                    break;
                case "partial-park":
                case "missing-executable":
                    var failing = step == "partial-park" ? command : StartInfo(@"C:\xe-definitely-missing\tool.exe");
                    _ = AssertEx.Throws<InvalidOperationException>(() => api.CreateSuspended(job, failing));
                    AssertEx.Empty(native.Commands, "a command was created");
                    AssertEx.Equal(baseline, HandleCount(native.Helpers[0]), "a parked value stayed in the helper");
                    AssertEx.False(WaitSignalled(native.Helpers[0], TimeSpan.Zero), "a live helper is kept after a plain failure");
                    break;
                case "post-create":
                    _ = AssertEx.Throws<InvalidOperationException>(() => api.CreateSuspended(job, command));
                    AssertEx.True(WaitSignalled(native.Commands[0], TimeSpan.FromSeconds(5)), "the created command was not ended by the launch");
                    AssertEx.True(WaitSignalled(native.Helpers[0], TimeSpan.FromSeconds(5)), "the suspect helper was not retired");
                    break;
                case "unconfirmed-exit":
                    native.HoldCommands = true;
                    AssertEx.Contains(AssertEx.Throws<InvalidOperationException>(() => api.CreateSuspended(job, command)).Message, "exit not confirmed");
                    native.Before = null;
                    var refused = AssertEx.Throws<InvalidOperationException>(() => api.CreateSuspended(job, StartInfo($"cmd /d /c echo x>\"{marker}\"")));
                    AssertEx.Contains(refused.Message, "has not exited");
                    AssertEx.False(File.Exists(marker), "a launch ran beside an unconfirmed process");
                    native.ReleaseHolds();
                    native.HoldCommands = false;
                    api.CreateSuspended(job, command).Dispose();
                    AssertEx.True(WaitSignalled(native.Commands[0], TimeSpan.Zero), "the held command had not exited when it was released");
                    break;
                case "child-construction":
                    WindowsJobCreatedProcess? adopted = null;
                    using (var ends = new TestPipeEnds())
                    {
                        _ = AssertEx.Throws<IOException>(() => parent.CreateCommand(job, command, ends.Stdin, ends.Stdout, ends.Stderr, created =>
                        {
                            adopted = created;
                            throw new IOException("injected by the test");
                        }));
                    }

                    AssertEx.True(WaitSignalled(native.Commands[0], TimeSpan.FromSeconds(5)), "the command was not ended after its child failed to build");
                    var handedOver = AssertEx.NotNull(adopted);
                    AssertEx.True(handedOver.Thread.IsClosed, "the thread handle is closed");
                    AssertEx.True(handedOver.Process.IsClosed, "the process handle is released after a confirmed exit");
                    break;
                default:
                    native.HoldCommands = true;
                    using (var outside = new OutsideTheJobApi(api))
                    {
                        var failure = AssertEx.Throws<InvalidOperationException>(() => new WindowsJobLauncher(outside).Start(command, WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount)));
                        AssertEx.Contains(failure.Message, "not a member");
                        AssertEx.Contains(failure.Message, "exit not confirmed");
                        AssertEx.Contains(AssertEx.Throws<InvalidOperationException>(() => api.CreateSuspended(job, command)).Message, "has not exited");
                        native.ReleaseHolds();
                        native.HoldCommands = false;
                        api.CreateSuspended(job, command).Dispose();
                        AssertEx.True(WaitSignalled(native.Commands[0], TimeSpan.Zero), "the outside command had not exited when it was released");
                    }

                    break;
            }

            api.Terminate(job);
            await AssertEx.EventuallyAsync(() => ActiveMembers(job) == 0, Settle, "the test job still has members");
            var liveHelpers = native.Helpers.Count(static helper => !WaitSignalled(helper, TimeSpan.Zero));
            await AssertEx.EventuallyAsync(() => ActiveMembers(AssertEx.NotNull(native.ParentJob)) == liveHelpers, Settle, "the parent job holds a process besides its live helpers");
        }
        finally
        {
            native.ReleaseHolds();
            File.Delete(marker);
            try
            {
                api.Terminate(job);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Already terminated above; closing the kill-on-close job is the backstop.
            }
        }
    }

    [Test]
    public async Task AHandleParentThatCannotBeVerified_WithdrawsTheCeilings_WithItsReason()
    {
        using var native = new RecordingLaunchNative
        {
            Before = static call =>
            {
                if (call == "IsProcessInJob#1")
                {
                    throw new System.ComponentModel.Win32Exception(31, "injected by the test");
                }
            }
        };
        using var parent = new WindowsJobHandleParent(native);

        var result = WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(new WindowsJobApi(parent)));

        AssertEx.False(result.Supported);
        AssertEx.Contains(AssertEx.NotNull(result.Reason), "the handle parent");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ACommand_RunsWithTheEnginesUserSessionIntegrityAffinityAndPriority()
    {
        using var native = new RecordingLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        using var process = new WindowsJobLauncher(new WindowsJobApi(parent)).Start(StartInfo("cmd /d /q /k"), WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount));
        try
        {
            using var engine = Process.GetCurrentProcess();
            var command = native.Commands[0];

            AssertEx.Equal(TokenSid(engine.SafeHandle, TokenUser), TokenSid(command, TokenUser), "the command runs as another user");
            AssertEx.Equal(TokenSid(engine.SafeHandle, TokenIntegrityLevel), TokenSid(command, TokenIntegrityLevel), "the command runs at another integrity level");
            AssertEx.True(ProcessIdToSessionId((uint)Environment.ProcessId, out var engineSession), "the engine's session id");
            AssertEx.True(ProcessIdToSessionId((uint)process.Id, out var commandSession), "the command's session id");
            AssertEx.Equal(engineSession, commandSession, "the command runs in another session");
            AssertEx.True(GetProcessAffinityMask(engine.SafeHandle, out var engineAffinity, out _), "the engine's affinity");
            AssertEx.True(GetProcessAffinityMask(command, out var commandAffinity, out _), "the command's affinity");
            AssertEx.Equal(engineAffinity, commandAffinity, "the command runs under another affinity");
            AssertEx.Equal(GetPriorityClass(engine.SafeHandle), GetPriorityClass(command), "the command runs under another priority class");
        }
        finally
        {
            process.Terminate();
        }

        await Task.CompletedTask;
    }

    /// <summary>Three launches through one handle parent: each command gets the priority, and a restricted affinity, the engine reports then.</summary>
    /// <remarks>The later reports are injected through the seam, so this host's own affinity never changes.</remarks>
    [Test]
    public async Task EachCommand_GetsTheAffinityAndPriorityTheEngineReportsAtItsLaunch()
    {
        using var native = new RecordingLaunchNative();
        using var parent = new WindowsJobHandleParent(native);
        var api = new EngineSchedulingApi(new WindowsJobApi(parent));
        var launcher = new WindowsJobLauncher(api);
        var limits = WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount);
        using var engine = Process.GetCurrentProcess();
        AssertEx.True(GetProcessAffinityMask(engine.SafeHandle, out var engineAffinity, out var systemAffinity), "the engine's affinity");
        AssertEx.Equal(systemAffinity, engineAffinity, "this test needs an unrestricted host, so the helper's snapshot is the whole system mask");
        var lowest = engineAffinity & (~engineAffinity + 1);
        AssertEx.NotEqual(engineAffinity, lowest, $"this test needs at least two logical processors; the host's mask is 0x{engineAffinity:X}");
        var hostPriority = GetPriorityClass(engine.SafeHandle);
        // Runners start test hosts at other classes than Normal, so the two injected classes are picked to differ from this host's.
        uint[] injected = [.. new[] { IdlePriorityClass, BelowNormalPriorityClass, NormalPriorityClass }.Where(priority => priority != hostPriority).Take(2)];
        var restricted = new WindowsJobScheduling { AffinityMask = lowest, SystemAffinityMask = systemAffinity, PriorityClass = injected[0] };
        var unrestricted = new WindowsJobScheduling { AffinityMask = systemAffinity, SystemAffinityMask = systemAffinity, PriorityClass = injected[1] };

        using var first = launcher.Start(StartInfo("cmd /d /q /k"), limits);
        api.Report = _ => restricted;
        using var second = launcher.Start(StartInfo("cmd /d /q /k"), limits);
        api.Report = _ => unrestricted;
        using var third = launcher.Start(StartInfo("cmd /d /q /k"), limits);
        try
        {
            AssertEx.Equal(engineAffinity, api.Read[0].AffinityMask, "the first launch read the engine's current affinity");
            AssertEx.Equal(hostPriority, api.Read[0].PriorityClass, "the first launch read the engine's current priority class");
            AssertEx.Equal(lowest.ToString("X", CultureInfo.InvariantCulture), string.Join(',', api.Affinities.Select(static mask => mask.ToString("X", CultureInfo.InvariantCulture))), "only the restricted report set an affinity, exactly as reported");
            AssertEx.Equal(string.Join(',', hostPriority, injected[0], injected[1]), string.Join(',', api.Priorities), "each launch set the priority class reported then");
            AssertEx.Equal(engineAffinity, AffinityOf(native.Commands[0]), "the first command runs under another affinity");
            AssertEx.Equal(hostPriority, GetPriorityClass(native.Commands[0]), "the first command runs under another priority class");
            AssertEx.Equal(lowest, AffinityOf(native.Commands[1]), "the second command kept the helper's snapshot, not the engine's report");
            AssertEx.Equal(injected[0], GetPriorityClass(native.Commands[1]), "the second command kept the helper's priority, not the engine's report");
            AssertEx.Equal(engineAffinity, AffinityOf(native.Commands[2]), "an unrestricted report left the third command on the whole system");
            AssertEx.Equal(injected[1], GetPriorityClass(native.Commands[2]), "the third command kept the helper's priority, not the engine's report");
            AssertEx.Equal(1, native.Helpers.Count, "every command came from one handle parent");
        }
        finally
        {
            first.Terminate();
            second.Terminate();
            third.Terminate();
        }

        await Task.CompletedTask;
    }

    // The negative control enters the launch window only through here, so it runs against another launcher by swapping this body.
    private static WindowsJobProcess StartInsideTheHandleWindow(string commandLine, Action insideWindow, List<IDisposable> owned)
    {
        var native = new RecordingLaunchNative();
        owned.Add(native);
        var parent = new WindowsJobHandleParent(native);
        owned.Add(parent);
        native.Before = call =>
        {
            if (call == "CreateCommand#1")
            {
                insideWindow();
            }
        };
        return new WindowsJobLauncher(new WindowsJobApi(parent)).Start(StartInfo(commandLine), WindowsJobLimits.Map(2048, 64, null, Environment.ProcessorCount));
    }

    private static void DisposeAll(List<IDisposable> owned)
    {
        for (var index = owned.Count - 1; index >= 0; index--)
        {
            owned[index].Dispose();
        }
    }

    private static nuint AffinityOf(SafeHandle process)
    {
        AssertEx.True(GetProcessAffinityMask(process, out var mask, out _), $"GetProcessAffinityMask failed ({Marshal.GetLastPInvokeError()})");
        return mask;
    }

    private static int PeekError(SafeHandle pipe) =>
        PeekNamedPipe(pipe, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError();

    private static uint Available(SafeHandle pipe)
    {
        AssertEx.True(PeekNamedPipe(pipe, IntPtr.Zero, 0, IntPtr.Zero, out var available, IntPtr.Zero), $"PeekNamedPipe failed ({Marshal.GetLastPInvokeError()})");
        return available;
    }

    private static bool WaitSignalled(SafeHandle process, TimeSpan timeout) =>
        WindowsJobNative.WaitForSingleObject(process, (uint)timeout.TotalMilliseconds) == 0;

    private static bool InJob(SafeHandle process, SafeHandle job)
    {
        if (!WindowsJobNative.IsProcessInJob(process, job, out var member))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return member;
    }

    private static int HandleCount(SafeHandle process)
    {
        AssertEx.True(GetProcessHandleCount(process, out var count), $"GetProcessHandleCount failed ({Marshal.GetLastPInvokeError()})");
        return checked((int)count);
    }

    private static uint ActiveMembers(SafeHandle job) =>
        WindowsJobNative.QueryInformation<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(job, JobObjectBasicAccountingInformation).ActiveProcesses;

    // TOKEN_USER and TOKEN_MANDATORY_LABEL both start with the SID pointer of a SID_AND_ATTRIBUTES.
    private static string TokenSid(SafeHandle process, int informationClass)
    {
        AssertEx.True(OpenProcessToken(process, TokenQuery, out var token), $"OpenProcessToken failed ({Marshal.GetLastPInvokeError()})");
        using (token)
        {
            _ = GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out var length);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                AssertEx.True(GetTokenInformation(token, informationClass, buffer, length, out _), $"GetTokenInformation failed ({Marshal.GetLastPInvokeError()})");
                return new System.Security.Principal.SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int TokenIntegrityLevel = 25;
    private const uint IdlePriorityClass = 0x40;
    private const uint BelowNormalPriorityClass = 0x4000;
    private const uint NormalPriorityClass = 0x20;

    /// <summary>Three engine-owned child ends, as <c>WindowsJobApi.CreateSuspended</c> would hand them over.</summary>
    private sealed class TestPipeEnds : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        public TestPipeEnds()
        {
            Stdin = Pipe(readEnd: true);
            Stdout = Pipe(readEnd: false);
            Stderr = Pipe(readEnd: false);
        }

        public SafeFileHandle Stdin { get; }

        public SafeFileHandle Stdout { get; }

        public SafeFileHandle Stderr { get; }

        public void Dispose()
        {
            foreach (var handle in _handles)
            {
                handle.Dispose();
            }
        }

        private SafeFileHandle Pipe(bool readEnd)
        {
            AssertEx.True(WindowsJobNative.CreatePipe(out var read, out var write, IntPtr.Zero, 0), $"CreatePipe failed ({Marshal.GetLastPInvokeError()})");
            _handles.Add(read);
            _handles.Add(write);
            return readEnd ? read : write;
        }
    }

    private const uint HandleFlagInherit = 0x1;

    private static ProcessSandboxRuntimeProvider Provider() =>
        new(Options.Create(new LocalContainerOptions()), TimeProvider.System);

    private static SandboxCreateRequest Request(SandboxResourceLimits limits) =>
        new()
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "windows-job-" + Guid.NewGuid().ToString("N"),
                ManifestVersion = 1
            },
            RuntimeProfile = "agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            ResourceLimits = limits
        };

    private static SandboxCommandRequest Cmd(string id, params string[] command) =>
        new()
        {
            ExecutionId = "windows-job-" + id,
            Executable = "cmd.exe",
            Arguments = ["/d", "/c", .. command],
            Timeout = TimeSpan.FromSeconds(60)
        };

    private static SandboxCommandRequest PowerShell(string id, string script) =>
        new()
        {
            ExecutionId = "windows-job-" + id,
            Executable = "powershell.exe",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
            Timeout = TimeSpan.FromSeconds(60)
        };

    // Two powershell spinners pinned to processors 0 and 1 in a job with these limits: the cores the job uses over a 4 s window.
    private static async Task<double> SpinnerCoresAsync(WindowsJobLimits limits)
    {
        const string spinners = "1..2 | ForEach-Object { [powershell]::Create().AddScript('[Console]::Out.WriteLine(''spinning''); [Console]::Out.Flush(); while ($true) {}').BeginInvoke() } | Out-Null; Start-Sleep 60";
        using var process = Launch($"cmd /d /c start \"\" /b /wait /affinity 3 powershell -NoProfile -NonInteractive -Command \"{spinners}\"", limits);
        try
        {
            // Both spinners announce themselves once they run, so the window below measures spinning, not powershell's start. One reader:
            // a second one could miss a line the first already buffered.
            using (var reader = new StreamReader(process.StandardOutput, leaveOpen: true))
            using (var announced = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                AssertEx.Equal("spinning", await reader.ReadLineAsync(announced.Token));
                AssertEx.Equal("spinning", await reader.ReadLineAsync(announced.Token));
            }

            // real-timer: a CPU measurement window, not a wait for an event; the job's CPU time is read at both ends of it.
            var before = JobCpuTime(process.Job);
            var clock = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(4));
            return (JobCpuTime(process.Job) - before).TotalSeconds / clock.Elapsed.TotalSeconds;
        }
        finally
        {
            process.Terminate();
        }
    }

    private static WindowsJobProcess Launch(string commandLine, WindowsJobLimits limits) =>
        new WindowsJobLauncher().Start(StartInfo(commandLine), limits);

    private static WindowsJobStartInfo StartInfo(string commandLine)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        return new WindowsJobStartInfo
        {
            CommandLine = commandLine,
            WorkingDirectory = Path.GetTempPath(),
            Environment = environment,
            // These tests read the engine's stdin end or keep an interactive cmd alive on it.
            RedirectStandardInput = true
        };
    }

    private static async Task<string> ReadLineAsync(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("the command printed nothing");
    }

    private static bool IsMember(int processId, SafeHandle job)
    {
        using var process = Process.GetProcessById(processId);
        if (!WindowsJobNative.IsProcessInJob(process.SafeHandle, job, out var member))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return member;
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static TimeSpan JobCpuTime(SafeHandle job)
    {
        var accounting = WindowsJobNative.QueryInformation<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(job, JobObjectBasicAccountingInformation);
        return TimeSpan.FromTicks(accounting.TotalUserTime + accounting.TotalKernelTime);
    }

    private const int JobObjectBasicAccountingInformation = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(SafeHandle hObject, out uint lpdwFlags);

    [StructLayout(LayoutKind.Sequential)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    /// <summary>The real native calls; records each engine scheduling read and each affinity and priority set, and can report another engine value.</summary>
    private sealed class EngineSchedulingApi : IWindowsJobApi
    {
        private readonly WindowsJobApi _real;

        public EngineSchedulingApi(WindowsJobApi real)
        {
            _real = real;
        }

        /// <summary>Turns the engine's real values into the ones reported to the launcher.</summary>
        public Func<WindowsJobScheduling, WindowsJobScheduling> Report { get; set; } = static real => real;

        public List<WindowsJobScheduling> Read { get; } = [];

        public List<nuint> Affinities { get; } = [];

        public List<uint> Priorities { get; } = [];

        public int GetSystemProcessorCount() => _real.GetSystemProcessorCount();

        public SafeHandle CreateJob() => _real.CreateJob();

        public void SetExtendedLimits(SafeHandle job, WindowsJobLimits limits) => _real.SetExtendedLimits(job, limits);

        public void SetCpuRate(SafeHandle job, uint cpuRate) => _real.SetCpuRate(job, cpuRate);

        public WindowsJobLimits QueryLimits(SafeHandle job) => _real.QueryLimits(job);

        public SafeHandle AssociateCompletionPort(SafeHandle job) => _real.AssociateCompletionPort(job);

        public bool ReceivedMessage(SafeHandle port, uint message) => _real.ReceivedMessage(port, message);

        public WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo) => _real.CreateSuspended(job, startInfo);

        public bool IsProcessInJob(SafeHandle process, SafeHandle job) => _real.IsProcessInJob(process, job);

        public WindowsJobScheduling GetEngineScheduling()
        {
            var real = _real.GetEngineScheduling();
            Read.Add(real);
            return Report(real);
        }

        public void SetAffinityMask(SafeHandle process, nuint affinityMask)
        {
            Affinities.Add(affinityMask);
            _real.SetAffinityMask(process, affinityMask);
        }

        public void SetPriorityClass(SafeHandle process, uint priorityClass)
        {
            Priorities.Add(priorityClass);
            _real.SetPriorityClass(process, priorityClass);
        }

        public void Resume(SafeHandle thread) => _real.Resume(thread);

        public void Terminate(SafeHandle job) => _real.Terminate(job);

        public string? TerminateAndConfirm(SafeHandle process, int processId) => _real.TerminateAndConfirm(process, processId);

        public Task WaitForExitAsync(SafeHandle process) => _real.WaitForExitAsync(process);

        public int GetExitCode(SafeHandle process) => _real.GetExitCode(process);

        public bool TryCommit(SafeHandle process, ulong bytes, out int error) => _real.TryCommit(process, bytes, out error);
    }

    /// <summary>The real native calls, except that the child is created inside a decoy job instead of the command's.</summary>
    private sealed class OutsideTheJobApi : IWindowsJobApi, IDisposable
    {
        private readonly WindowsJobApi _real;
        private readonly SafeHandle _decoy;

        public OutsideTheJobApi()
            : this(new WindowsJobApi())
        {
        }

        public OutsideTheJobApi(WindowsJobApi real)
        {
            _real = real;
            _decoy = _real.CreateJob();
        }

        public int CreatedProcessId { get; private set; }

        public int GetSystemProcessorCount() => _real.GetSystemProcessorCount();

        public SafeHandle CreateJob() => _real.CreateJob();

        public void SetExtendedLimits(SafeHandle job, WindowsJobLimits limits) => _real.SetExtendedLimits(job, limits);

        public void SetCpuRate(SafeHandle job, uint cpuRate) => _real.SetCpuRate(job, cpuRate);

        public WindowsJobLimits QueryLimits(SafeHandle job) => _real.QueryLimits(job);

        public SafeHandle AssociateCompletionPort(SafeHandle job) => _real.AssociateCompletionPort(job);

        public bool ReceivedMessage(SafeHandle port, uint message) => _real.ReceivedMessage(port, message);

        public WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo)
        {
            var child = _real.CreateSuspended(_decoy, startInfo);
            CreatedProcessId = child.ProcessId;
            return child;
        }

        public bool IsProcessInJob(SafeHandle process, SafeHandle job) => _real.IsProcessInJob(process, job);

        public WindowsJobScheduling GetEngineScheduling() => _real.GetEngineScheduling();

        public void SetAffinityMask(SafeHandle process, nuint affinityMask) => _real.SetAffinityMask(process, affinityMask);

        public void SetPriorityClass(SafeHandle process, uint priorityClass) => _real.SetPriorityClass(process, priorityClass);

        public void Resume(SafeHandle thread) => throw new InvalidOperationException("a child that failed its membership check must never be resumed");

        public void Terminate(SafeHandle job) => _real.Terminate(job);

        public string? TerminateAndConfirm(SafeHandle process, int processId) => _real.TerminateAndConfirm(process, processId);

        public Task WaitForExitAsync(SafeHandle process) => _real.WaitForExitAsync(process);

        public int GetExitCode(SafeHandle process) => _real.GetExitCode(process);

        public bool TryCommit(SafeHandle process, ulong bytes, out int error) => _real.TryCommit(process, bytes, out error);

        public void Dispose() => _decoy.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafeHandle hNamedPipe, IntPtr lpBuffer, uint nBufferSize, IntPtr lpBytesRead, out uint lpTotalBytesAvail, IntPtr lpBytesLeftThisMessage);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(SafeHandle hProcess, out uint pdwHandleCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(SafeHandle hProcess, out nuint lpProcessAffinityMask, out nuint lpSystemAffinityMask);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetPriorityClass(SafeHandle hProcess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeHandle processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
}
