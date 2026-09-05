namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Runtime verification of the Windows Job Object containment path — the mirror of
///     <see cref="LinuxProcessGroupTreeKillTests" />, and the discharge of the operator-verification flag
///     <c>WindowsJobObjectProcessHandle</c> carries in its own remarks (<i>"real tree-kill behavior MUST be verified on
///     Windows 11"</i>). Before this file that class had no covering test of any kind.
/// </summary>
/// <remarks>
///     <para>
///         The Job Object is the ONLY orphan defence on Windows — there is no <c>setsid</c>/pgid fallback there — so the
///         failure it prevents is an <c>llama-server.exe</c> holding 8-14 GB of VRAM and a loopback port forever.
///     </para>
///     <para>
///         <b>Two distinct properties are proven here, and the second is the one that matters.</b>
///         <see cref="Launch_ThenTreeKill_ReapsDescendants_NoOrphan" /> covers the graceful path: managed code runs and
///         closes the job. <see cref="HardKillOfOwningProcess_ReapsDescendants_NoOrphan" /> covers the path the runbook
///         exists for — the owning process is destroyed by <c>TerminateProcess</c>, no managed code runs, and the tree
///         must still die because the kernel closed the last handle to a kill-on-close job. A graceful teardown passing
///         says nothing about that, which is exactly why closing the console window is not a valid manual check either
///         (<c>DesktopLifecycle</c> intercepts it and runs the full supervisor teardown).
///     </para>
/// </remarks>
public sealed class WindowsJobObjectTreeKillTests
{
    /// <summary>
    ///     Env var carrying the marker path to the in-process helper below. Absent on a normal suite run, so the helper
    ///     is inert; set only on the child test host that
    ///     <see cref="HardKillOfOwningProcess_ReapsDescendants_NoOrphan" /> spawns.
    /// </summary>
    internal const string HardKillMarkerVariable = "XE_JOBOBJECT_HARDKILL_MARKER";

    /// <summary>Upper bound on how long the spawned child host parks waiting to be terminated by its parent.</summary>
    private static readonly TimeSpan OrphanParkLimit = TimeSpan.FromMinutes(5);

    [Test]
    public async Task Launch_ThenTreeKill_ReapsDescendants_NoOrphan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows-only runtime verification; LinuxProcessGroupTreeKillTests covers the other branch.
            return;
        }

        var markerFile = NewMarkerPath();
        var launcher = new LlamaServerProcessLauncher(NullLogger<LlamaServerProcessLauncher>.Instance);
        var handle = launcher.Launch(BuildDescendantSpawningSpec(markerFile));
        var descendantPid = 0;

        try
        {
            descendantPid = await ReadDescendantPidAsync(markerFile);
            AssertEx.True(IsProcessAlive(descendantPid), "The descendant should be alive before tree-kill.");

            handle.TreeKill();

            // Closing a JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE job must terminate every process in it, not just the direct
            // child. The descendant is the orphan-risk process: it is what llama-server would be.
            await AssertEx.EventuallyAsync(() => !IsProcessAlive(descendantPid),
                TimeSpan.FromSeconds(10),
                "A descendant survived TreeKill — the Job Object did not contain the tree.");

            // TreeKill closes the job handle; Dispose must tolerate that rather than throwing on a second close.
            handle.TreeKill();
            handle.Dispose();
        }
        finally
        {
            handle.Dispose();
            TryKillPid(descendantPid);
            TryDelete(markerFile);
        }
    }

    /// <summary>
    ///     The runbook's check 1, automated. Spawns a second test host, has it contain a descendant in a real Job
    ///     Object, then destroys that host with <c>TerminateProcess</c> — the same primitive Task Manager's
    ///     <i>End task</i> issues, delivering no console-ctrl event and running no managed code — and asserts the
    ///     descendant is reaped anyway.
    /// </summary>
    [Test]
    public async Task HardKillOfOwningProcess_ReapsDescendants_NoOrphan()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var testHost = ResolveTestHostPath();
        AssertEx.True(testHost is not null,
            $"Could not locate the test host executable next to {AppContext.BaseDirectory}. This test cannot run "
            + "without it, and reporting that as a pass would hide the only automated check of the hard-kill path.");

        var markerFile = NewMarkerPath();
        var startInfo = new ProcessStartInfo
        {
            FileName = testHost!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--treenode-filter");
        startInfo.ArgumentList.Add($"/*/*/{nameof(WindowsJobObjectTreeKillTests)}/{nameof(HardKillHelper_ContainsDescendantThenWaitsToBeKilled)}");
        startInfo.Environment[HardKillMarkerVariable] = markerFile;

        using var host = Process.Start(startInfo)
                         ?? throw new InvalidOperationException("The helper test host did not start.");
        var descendantPid = 0;

        try
        {
            // Draining is required: the helper host writes to stdout and a full pipe would stall it before it ever
            // reaches the marker write.
            host.BeginOutputReadLine();
            host.BeginErrorReadLine();

            // Generous: this is a whole test-host start (assembly load + discovery), not a process spawn.
            descendantPid = await ReadDescendantPidAsync(markerFile, TimeSpan.FromSeconds(90));
            AssertEx.True(IsProcessAlive(descendantPid), "The descendant should be alive before the hard kill.");

            // entireProcessTree: false is the whole point. Killing the tree here would prove nothing about the Job
            // Object — it would be this test doing the reaping. Only the owning process is destroyed.
            host.Kill(entireProcessTree: false);
            await host.WaitForExitAsync();

            await AssertEx.EventuallyAsync(() => !IsProcessAlive(descendantPid),
                TimeSpan.FromSeconds(15),
                "A descendant survived a hard kill of the process that owned the Job Object. On Windows this is the "
                + "orphan defence in full — nothing else reaps it, and a real llama-server would hold its VRAM and "
                + "port until the machine was rebooted.");
        }
        finally
        {
            TryKillPid(descendantPid);
            TryDelete(markerFile);
        }
    }

    /// <summary>
    ///     Inert on a normal suite run. Only when <see cref="HardKillMarkerVariable" /> is set — which happens solely on
    ///     the child host the hard-kill test spawns — does it contain a descendant, publish its PID, and then park,
    ///     waiting to be terminated. It deliberately never disposes the handle: the point is that no managed cleanup
    ///     runs.
    /// </summary>
    [Test]
    public async Task HardKillHelper_ContainsDescendantThenWaitsToBeKilled()
    {
        var markerFile = Environment.GetEnvironmentVariable(HardKillMarkerVariable);
        if (string.IsNullOrWhiteSpace(markerFile) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var launcher = new LlamaServerProcessLauncher(NullLogger<LlamaServerProcessLauncher>.Instance);
        _ = launcher.Launch(BuildDescendantSpawningSpec(markerFile));

        // real-timer: this branch runs in a REAL child host that the parent test kills with a Job Object. There is
        // nothing to signal — the point is that no managed cleanup runs — so it parks. Bounded so a parent that died
        // before killing us cannot leave a host running forever on a developer's machine.
        await Task.Delay(OrphanParkLimit);
    }

    /// <summary>
    ///     A spec whose "server" is Windows PowerShell spawning a longer-lived grandchild and recording its PID. The
    ///     grandchild is what makes this a containment test rather than a kill-the-child test: only a Job Object reaps a
    ///     process the handle never knew about.
    /// </summary>
    private static LlamaServerLaunchSpec BuildDescendantSpawningSpec(string markerFile)
    {
        // Windows PowerShell 5.1 by absolute path: in-box on every Windows 11 install, and immune to a pwsh that is
        // absent or shadowed on PATH.
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script =
            $"$g = Start-Process -FilePath cmd.exe -ArgumentList '/c','ping -n 900 127.0.0.1' -PassThru -WindowStyle Hidden; "
            + $"Set-Content -LiteralPath '{markerFile}' -Value $g.Id; "
            + "Start-Sleep -Seconds 900";

        return new LlamaServerLaunchSpec("test-model",
            ModelRole.Chat,
            powershell,
            ["-NoProfile", "-NonInteractive", "-Command", script],
            Port: 0,
            Path.GetTempPath());
    }

    private static string NewMarkerPath()
    {
        return Path.Combine(Path.GetTempPath(), $"xe-jobobject-test-{Guid.NewGuid():N}.pid");
    }

    private static async Task<int> ReadDescendantPidAsync(string markerFile, TimeSpan? timeout = null)
    {
        await AssertEx.EventuallyAsync(() => File.Exists(markerFile) && new FileInfo(markerFile).Length > 0,
            timeout ?? TimeSpan.FromSeconds(30),
            $"The descendant PID marker was never written to {markerFile}.");

        var text = (await File.ReadAllTextAsync(markerFile)).Trim();
        return int.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     The MTP test host next to this assembly. Preferred over <see cref="Environment.ProcessPath" />, which is
    ///     <c>dotnet</c> itself whenever the suite is bridged through <c>dotnet test</c>.
    /// </summary>
    private static string? ResolveTestHostPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory,
            Path.GetFileNameWithoutExtension(typeof(WindowsJobObjectTreeKillTests).Assembly.Location) + ".exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // No such process.
        }
        catch (InvalidOperationException)
        {
            return false; // Exited between lookup and read.
        }
    }

    private static void TryKillPid(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup: the assertions above already decided the verdict.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
