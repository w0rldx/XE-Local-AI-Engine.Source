namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Runtime verification of the Windows Job Object containment path — the mirror of
///     <see cref="LinuxProcessGroupTreeKillTests" />, and the discharge of the operator-verification flag
///     <see cref="WindowsJobObjectProcessHandle" /> carries in its own remarks (<i>"real tree-kill behavior MUST be
///     verified on Windows 11"</i>). The llama-server, sd-server and whisper-server launchers all contain their child
///     through that one class, so this proof covers all three.
/// </summary>
/// <remarks>
///     <para>
///         The Job Object is the ONLY orphan defence on Windows — there is no <c>setsid</c>/pgid fallback there — so the
///         failure it prevents is a runtime server such as <c>llama-server.exe</c> holding 8-14 GB of VRAM and a loopback port forever.
///     </para>
///     <para>
///         <b>Two distinct properties are proven here, and the second is the one that matters.</b>
///         <see cref="Wrap_ThenTreeKill_ReapsDescendants_NoOrphan" /> covers the graceful path: managed code runs and
///         closes the job. <see cref="HardKillOfOwningProcess_ReapsDescendants_NoOrphan" /> covers the path the runbook
///         exists for — the owning process is destroyed by <c>TerminateProcess</c>, no managed code runs, and the tree
///         must still die because the kernel closed the last handle to a kill-on-close job. A graceful teardown passing
///         says nothing about that, which is exactly why closing the console window is not a valid manual check either
///         (<c>DesktopLifecycle</c> intercepts it and runs the full supervisor teardown).
///     </para>
/// </remarks>
[Category(TestCategories.Integration)]
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

    // Windows-only runtime verification; LinuxProcessGroupTreeKillTests covers the other branch.
    [Test]
    [RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task Wrap_ThenTreeKill_ReapsDescendants_NoOrphan()
    {
        var markerFile = NewMarkerPath();
        var handle = ContainDescendantSpawner(markerFile);
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
    [RunOn(OS.Windows)]
    public async Task HardKillOfOwningProcess_ReapsDescendants_NoOrphan()
    {
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
    ///     The child host's entry point, not a test of its own. It stays a <c>[Test]</c> only because the hard-kill
    ///     test reaches it through <c>--treenode-filter</c>; on every other run it reports a visible skip rather than a
    ///     pass. Only when <see cref="HardKillMarkerVariable" /> is set — which happens solely on the child host the
    ///     hard-kill test spawns — does it contain a descendant, publish its PID, and then park, waiting to be
    ///     terminated. It deliberately never disposes the handle: the point is that no managed cleanup runs.
    /// </summary>
    [Test]
    [RunOn(OS.Windows)]
    [SupportedOSPlatform("windows")]
    public async Task HardKillHelper_ContainsDescendantThenWaitsToBeKilled()
    {
        var markerFile = Environment.GetEnvironmentVariable(HardKillMarkerVariable);
        if (string.IsNullOrWhiteSpace(markerFile))
        {
            Skip.Test($"Child-host helper for {nameof(HardKillOfOwningProcess_ReapsDescendants_NoOrphan)}; it runs only with {HardKillMarkerVariable} set.");
        }

        _ = ContainDescendantSpawner(markerFile);

        // real-timer: this branch runs in a REAL child host that the parent test kills with a Job Object. There is
        // nothing to signal — the point is that no managed cleanup runs — so it parks. Bounded so a parent that died
        // before killing us cannot leave a host running forever on a developer's machine.
        await Task.Delay(OrphanParkLimit);
    }

    /// <summary>
    ///     Starts Windows PowerShell spawning a longer-lived grandchild and recording its PID, and contains it in a Job
    ///     Object exactly as the runtime launchers contain their servers. The grandchild is what makes this a containment
    ///     test rather than a kill-the-child test: only a Job Object reaps a process the handle never knew about.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static WindowsJobObjectProcessHandle ContainDescendantSpawner(string markerFile)
    {
        // Windows PowerShell 5.1 by absolute path: in-box on every Windows 11 install, and immune to a pwsh that is
        // absent or shadowed on PATH.
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script =
            $"$g = Start-Process -FilePath cmd.exe -ArgumentList '/c','ping -n 900 127.0.0.1' -PassThru -WindowStyle Hidden; "
            + $"Set-Content -LiteralPath '{markerFile}' -Value $g.Id; "
            + "Start-Sleep -Seconds 900";

        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        return WindowsJobObjectProcessHandle.Wrap(process,
            static ex => new InvalidOperationException("The test process could not be contained.", ex));
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
