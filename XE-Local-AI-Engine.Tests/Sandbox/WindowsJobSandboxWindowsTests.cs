namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
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
    public async Task TheProcessCeiling_StopsAChainPastIt_AndAdmitsOneInsideIt()
    {
        using var provider = Provider();
        var handle = await provider.CreateOrAttachAsync(Request(new SandboxResourceLimits { PidsLimit = 4 }));
        try
        {
            var inside = await provider.ExecuteAsync(handle, Cmd("chain-2", "cmd", "/d", "/c", "exit", "7"));
            var past = await provider.ExecuteAsync(handle, Cmd("chain-5", "cmd", "/d", "/c", "cmd", "/d", "/c", "cmd", "/d", "/c", "cmd", "/d", "/c", "exit", "7"));

            AssertEx.Equal(7, inside.ExitCode, inside.StandardError);
            AssertEx.NotEqual(7, past.ExitCode, "a chain of five processes ran inside a four-process job");
        }
        finally
        {
            await provider.KillAsync(handle);
        }
    }

    /// <summary>One core's worth under a two-processor affinity: the rate is against the WHOLE system, not the processors a member may use.</summary>
    /// <remarks>
    ///     <c>start /affinity 3</c> pins powershell from creation, so this test host never runs under a changed affinity (ADR 0025, CPU
    ///     rule). The denominator is read in this unrestricted host, where the system and affinity counts agree, so this test cannot
    ///     catch <c>WindowsJobApi.GetSystemProcessorCount</c> turning affinity-relative.
    /// </remarks>
    [Test]
    public async Task TheCpuCeiling_HoldsOneCore_UnderRestrictedAffinity()
    {
        var system = new WindowsJobLauncher().SystemProcessorCount;
        AssertEx.True(system >= 2, $"this test needs at least two logical processors; the host has {system}");
        const string spinners = "1..2 | ForEach-Object { [powershell]::Create().AddScript('[Console]::Out.WriteLine(''spinning''); [Console]::Out.Flush(); while ($true) {}').BeginInvoke() } | Out-Null; Start-Sleep 60";
        using var process = Launch($"cmd /d /c start \"\" /b /wait /affinity 3 powershell -NoProfile -NonInteractive -Command \"{spinners}\"",
            WindowsJobLimits.Map(4096, 64, 1, system));
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
            var cores = (JobCpuTime(process.Job) - before).TotalSeconds / clock.Elapsed.TotalSeconds;

            AssertEx.True(cores > 0.3, $"the spinners used {cores:F2} cores; the measurement saw no work");
            AssertEx.True(cores <= 1.35, $"the job used {cores:F2} cores under a one-core ceiling");
        }
        finally
        {
            process.Terminate();
        }
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
    ///     A process outside the job holding stdout open ends the command at the drain budget, the unterminated line the root printed is
    ///     still delivered and marks stdout incomplete, and both pumps finish.
    /// </summary>
    /// <remarks>
    ///     The root duplicates its own stdout into this test host, which is outside the job, then exits. Windows documents cancelling a
    ///     blocked read on another thread for overlapped I/O, so the engine reads stdout and stderr overlapped (ADR 0025).
    /// </remarks>
    [Test]
    public async Task AStdoutHeldOutsideTheJob_EndsAtTheDrainBudget_DeliversThePendingLine_AndBothPumpsFinish()
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
        var held = IntPtr.Zero;
        try
        {
            var clock = Stopwatch.StartNew();
            var exitCode = await child.WaitForExitAsync(CancellationToken.None);
            var returnedAfter = clock.Elapsed;
            lock (errors)
            {
                held = errors.Select(static line => long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? checked((IntPtr)value) : IntPtr.Zero)
                             .FirstOrDefault(static value => value != IntPtr.Zero);
            }

            AssertEx.Equal(0, exitCode, $"the root could not hand its stdout over: {string.Join(" | ", errors)}");
            AssertEx.True(held != IntPtr.Zero, "the root reported no duplicated handle");
            AssertEx.True(returnedAfter >= MxcChildProcess.PumpDrainBudget - TimeSpan.FromSeconds(1), $"returned after {returnedAfter}, before the held pipe's drain budget");
            AssertEx.True(returnedAfter <= MxcChildProcess.PumpDrainBudget + TimeSpan.FromSeconds(30), $"returned after {returnedAfter}: the held read was not cancelled");
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
            if (held != IntPtr.Zero)
            {
                _ = CloseHandle(held);
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

    /// <summary>The real native calls, except that the child is created inside a decoy job instead of the command's.</summary>
    private sealed class OutsideTheJobApi : IWindowsJobApi, IDisposable
    {
        private readonly WindowsJobApi _real = new();
        private readonly SafeHandle _decoy;

        public OutsideTheJobApi()
        {
            _decoy = _real.CreateJob();
        }

        public int CreatedProcessId { get; private set; }

        public int GetSystemProcessorCount() => _real.GetSystemProcessorCount();

        public SafeHandle CreateJob() => _real.CreateJob();

        public void SetExtendedLimits(SafeHandle job, WindowsJobLimits limits) => _real.SetExtendedLimits(job, limits);

        public void SetCpuRate(SafeHandle job, uint cpuRate) => _real.SetCpuRate(job, cpuRate);

        public WindowsJobLimits QueryLimits(SafeHandle job) => _real.QueryLimits(job);

        public WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo)
        {
            var child = _real.CreateSuspended(_decoy, startInfo);
            CreatedProcessId = child.ProcessId;
            return child;
        }

        public bool IsProcessInJob(SafeHandle process, SafeHandle job) => _real.IsProcessInJob(process, job);

        public void Resume(SafeHandle thread) => throw new InvalidOperationException("a child that failed its membership check must never be resumed");

        public void Terminate(SafeHandle job) => _real.Terminate(job);

        public void TerminateProcess(SafeHandle process) => _real.TerminateProcess(process);

        public Task WaitForExitAsync(SafeHandle process) => _real.WaitForExitAsync(process);

        public int GetExitCode(SafeHandle process) => _real.GetExitCode(process);

        public bool TryCommit(SafeHandle process, ulong bytes, out int error) => _real.TryCommit(process, bytes, out error);

        public void Dispose() => _decoy.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
