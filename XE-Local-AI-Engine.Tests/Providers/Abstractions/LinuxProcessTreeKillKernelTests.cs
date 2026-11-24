namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>The pidfd tree walk on the real kernel: a shell's child, a setsid child and a grandchild all die, and an exited root sends nothing.</summary>
/// <remarks>
///     Every expected process is announced on stdout and pinned by a verified pidfd before the walk. Each child lives only while the test's stdin
///     pipe is open, so cleanup never needs a numeric signal; it signals only the verified pins.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class LinuxProcessTreeKillKernelTests
{
    // Every child blocks reading the test's stdin pipe (fd 3: an asynchronous list's own stdin is /dev/null), so closing that pipe, or
    // this host dying, ends the whole tree from launch on without a signal, whatever the pins or the code under test did.
    private const string Script = """
                                  exec 3<&0
                                  cat <&3 >/dev/null 2>&1 & echo "c $!"
                                  setsid cat <&3 >/dev/null 2>&1 & echo "s $!"
                                  ( cat <&3 >/dev/null 2>&1 & echo "g $!"; wait ) & echo "m $!"
                                  wait
                                  """;

    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task Kill_RealTree_KillsEveryDescendantIncludingAnotherGroupAndAGrandchild()
    {
        SkipWithoutPinnedWalk();
        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", Script])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var pins = new Dictionary<int, LinuxPidFd>();
        try
        {
            Pin(pins, process.Id, expectedParent: Environment.ProcessId);
            var pids = await ReadAnnouncedPidsAsync(process);
            Pin(pins, pids["c"], process.Id);
            Pin(pins, pids["s"], process.Id);
            Pin(pins, pids["m"], process.Id);
            Pin(pins, pids["g"], pids["m"]);

            var result = LinuxProcessTreeKill.Kill(process);

            AssertEx.Equal(LinuxProcessTreeKillResult.Complete, result);
            await AssertEx.EventuallyAsync(() => pins.All(static pin => IsDead(pin.Value, pin.Key)), TimeSpan.FromSeconds(10),
                "a descendant survived the tree kill");
        }
        finally
        {
            // EOF on fd 3 ends every child, pinned or not; the validated pins then only hurry the ones they name.
            process.StandardInput.Close();
            foreach (var pin in pins.Values)
            {
                _ = pin.Send(9, group: false);
                pin.Dispose();
            }
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task Kill_ExitedThenDisposedRoot_IsCompleteAndSendsNothing()
    {
        var process = Process.Start(new ProcessStartInfo("/bin/true")
        {
            UseShellExecute = false
        })!;
        await process.WaitForExitAsync();

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, LinuxProcessTreeKill.Kill(process));
        process.Dispose();
        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, LinuxProcessTreeKill.Kill(process));
    }

    private static void SkipWithoutPinnedWalk()
    {
        if (!LinuxProcessTreeKillCalls.Real.HostVerifiable())
        {
            Skip.Test("BLOCKED: this test host is PID 1 or /proc numbers another PID namespace, so the walk takes the runtime fallback by design.");
        }

        using var self = LinuxPidFd.Open(Environment.ProcessId);
        if (self.OpenStatus != LinuxPidFdOpenStatus.Opened)
        {
            Skip.Test($"BLOCKED: pidfd_open is {self.OpenStatus} here, so the pinned walk cannot run.");
        }
    }

    private static async Task<Dictionary<string, int>> ReadAnnouncedPidsAsync(Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pids = new Dictionary<string, int>(StringComparer.Ordinal);
        while (pids.Count < 4 && await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
        {
            var parts = line.Split(' ');
            pids[parts[0]] = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        AssertEx.Equal(4, pids.Count, "the shell did not announce every process");
        return pids;
    }

    /// <summary>Pins a pid and keeps the pin only once it is proven the announced process: parent read after the open, alive through the pin.</summary>
    private static void Pin(Dictionary<int, LinuxPidFd> pins, int pid, int expectedParent)
    {
        var pin = LinuxPidFd.Open(pid);
        var stat = LinuxProcStat.Read(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/stat"));
        if (pin.OpenStatus != LinuxPidFdOpenStatus.Opened || stat.ParentProcessId != expectedParent || pin.Send(0, group: false).Status != LinuxPidFdSendStatus.Sent)
        {
            pin.Dispose();
            AssertEx.True(false, $"pid {pid} is not the announced live child of {expectedParent} ({pin.OpenStatus}, parent {stat.ParentProcessId})");
        }

        pins[pid] = pin;
    }

    // Gone, or a zombie: a pin that still reaches it means its pid is unreaped, so the stat read names it.
    private static bool IsDead(LinuxPidFd pin, int pid) =>
        pin.Send(0, group: false).Status == LinuxPidFdSendStatus.Gone
        || LinuxProcStat.Read(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/stat")).Exited;
}
