namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Runtime check of the Linux process-group tree-kill path, the branch a Linux environment can exercise: a real
///     shell under <c>setsid</c> forks a child, the shared <see cref="LinuxProcessGroupHandle" /> tree-kills it, and NO
///     descendant survives.
/// </summary>
/// <remarks>
///     The Windows Job Object branch is implemented to the <c>dotnet-pinvoke</c> standard but cannot run here — it is
///     flagged for operator verification on real Windows 11. This test guards the half that runs on Linux, for every
///     provider that launches through the shared handle.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class LinuxProcessGroupTreeKillTests
{
    // Linux-only runtime verification; the Windows path is operator-verified.
    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task Wrap_ThenTreeKill_KillsChildProcessGroup_NoOrphan()
    {
        // A shell that spawns a long-lived grandchild and writes its PID, then sleeps. setsid makes the shell a
        // group leader so kill(-pgid) must reap BOTH the shell and the grandchild.
        var markerFile = Path.Combine(Path.GetTempPath(), $"xe-pgid-test-{Guid.NewGuid():N}.pid");
        var script = $"sleep 600 & echo $! > '{markerFile}'; sleep 600";

        var handle = LinuxProcessGroupHandle.Wrap(StartUnderSetsid(script));
        try
        {
            await AssertEx.EventuallyAsync(() => File.Exists(markerFile) && new FileInfo(markerFile).Length > 0,
                TimeSpan.FromSeconds(5), "Grandchild PID marker was not written.");

            var grandchildPid = int.Parse((await File.ReadAllTextAsync(markerFile)).Trim(), CultureInfo.InvariantCulture);
            AssertEx.True(IsProcessAlive(grandchildPid), "Grandchild should be alive before tree-kill.");

            handle.TreeKill();

            // The whole process group must be gone — verify the grandchild (the orphan-risk process) is reaped.
            await AssertEx.EventuallyAsync(() => !IsProcessAlive(grandchildPid),
                TimeSpan.FromSeconds(5), "Grandchild survived tree-kill — orphaned process group.");
        }
        finally
        {
            handle.Dispose();
            TryDelete(markerFile);
        }
    }

    /// <summary>The leader dies of the group TERM while a member ignores it: the pin still reaches the member after the leader is reaped.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task TreeKill_LeaderExitsOnTerm_MemberIgnoringTerm_IsKilledThroughThePin()
    {
        using (var self = LinuxPidFd.Open(Environment.ProcessId))
        {
            if (self.OpenStatus != LinuxPidFdOpenStatus.Opened || self.Send(0, group: true).Status == LinuxPidFdSendStatus.GroupFlagUnsupported)
            {
                Skip.Test("BLOCKED: this kernel has no pidfd group signal (before 6.9), so the pinned group kill cannot be measured here.");
            }
        }

        var markerFile = Path.Combine(Path.GetTempPath(), $"xe-pgid-test-{Guid.NewGuid():N}.pid");
        // The member ignores TERM (inherited across exec) and has closed stdio; the leader waits on it and dies of the TERM.
        var script = $"( trap '' TERM; exec sleep 600 ) </dev/null >/dev/null 2>&1 & echo $! > '{markerFile}'; wait";
        var handle = LinuxProcessGroupHandle.Wrap(StartUnderSetsid(script));
        LinuxPidFd? member = null;
        try
        {
            await AssertEx.EventuallyAsync(() => File.Exists(markerFile) && new FileInfo(markerFile).Length > 0,
                TimeSpan.FromSeconds(5), "Member PID marker was not written.");
            var memberPid = int.Parse((await File.ReadAllTextAsync(markerFile)).Trim(), CultureInfo.InvariantCulture);
            member = LinuxPidFd.Open(memberPid);
            AssertEx.Equal(LinuxPidFdOpenStatus.Opened, member.OpenStatus, "the member must be alive before the kill");
            // Only once it exec'd sleep has it installed the TERM ignore the test is about.
            var comm = string.Create(CultureInfo.InvariantCulture, $"/proc/{memberPid}/comm");
            await AssertEx.EventuallyAsync(() => File.Exists(comm) && string.Equals(File.ReadAllText(comm).Trim(), "sleep", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                "The member never reached its exec.");

            AssertEx.True(handle.Terminate(), "the kill is complete only once the group is seen empty");
            AssertEx.True(handle.HasExited, "the leader died of the TERM");
            AssertEx.Equal(LinuxPidFdSendStatus.Gone, member.Send(0, group: false).Status, "the TERM-ignoring member was killed after its leader");
        }
        finally
        {
            // Cleanup independent of the code under test: only through the member's own pin.
            _ = member?.Send(9, group: false);
            member?.Dispose();
            handle.Dispose();
            TryDelete(markerFile);
        }
    }

    /// <summary>Starts <c>/bin/sh -c script</c> under <c>setsid</c>, exactly as the runtime launchers start their servers.</summary>
    [SupportedOSPlatform("linux")]
    private static Process StartUnderSetsid(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = SetsidLocator.ResolveAbsolutePath(),
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        return Process.Start(startInfo) ?? throw new InvalidOperationException("The shell did not start.");
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false; // No such process.
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
