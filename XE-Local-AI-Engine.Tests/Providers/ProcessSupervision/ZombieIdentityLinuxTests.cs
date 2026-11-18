namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>A real zombie read through <c>/proc</c>: its stat reads exited, its empty environment carries no run token, and its spawn receipt is a mismatch.</summary>
/// <remarks>The zombie's parent is a child this test holds a handle to; nothing is signalled but that parent.</remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class ZombieIdentityLinuxTests
{
    [Test]
    public async Task AZombie_ReadsExited_CarriesNoToken_AndItsReceiptIsAMismatch()
    {
        // sh forks true, then execs sleep in place: sleep never waits, so true stays a zombie for as long as sleep runs.
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("/bin/true & exec /bin/sleep 30");
        using var parent = Process.Start(startInfo) ?? throw new InvalidOperationException("/bin/sh did not start.");
        try
        {
            await AssertEx.EventuallyAsync(() => FindZombieChild(parent.Id, out _) > 0, TestBudgets.Contended, "The forked true never showed up as a zombie child.");
            // Read again: a zombie stays one while its parent never waits.
            var zombiePid = FindZombieChild(parent.Id, out var zombie);

            AssertEx.True(zombie.Exited, "State Z reads exited.");
            AssertEx.Null(LinuxTrainingProcessInspector.ReadRunToken(zombiePid), "A zombie's environment is unreadable or empty, by kernel: no token, so no contradiction.");
            AssertEx.Equal(StaleProcessKillResult.Mismatch, new OsStaleProcessScanner("true").KillIfSameProcess(zombiePid, "/usr/bin/true", zombie.StartTicks),
                "A zombie's receipt names no server; it is dropped instead of kept for ever on an unreadable exe link.");
        }
        finally
        {
            parent.Kill();
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            await parent.WaitForExitAsync(budget.Token);
        }
    }

    /// <summary>The pid of an exited child of <paramref name="parentPid" />, read from <c>/proc</c>, or 0 while there is none.</summary>
    private static int FindZombieChild(int parentPid, out ProcessStat zombie)
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                && LinuxProcFs.ReadStatOrNull(pid) is { Exited: true } stat
                && stat.ParentProcessId == parentPid)
            {
                zombie = stat;
                return pid;
            }
        }

        zombie = default;
        return 0;
    }
}
