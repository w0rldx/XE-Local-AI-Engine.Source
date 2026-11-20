namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static LinuxTrainingProcessGroupTests;
using OS = TUnit.Core.Enums.OS;

/// <summary><see cref="LinuxPidFd" /> against the real kernel: reaped reads gone, a live child and a whole group receive the signal, and the fd is close-on-exec.</summary>
/// <remarks>Every pid signalled is a child this test still holds a handle to, or a member guarded by its recorded start time.</remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class LinuxPidFdTests
{
    private const int Sigterm = 15;
    private const int SignalledBySigterm = 128 + Sigterm;

    [Test]
    public void Send_AfterTheProcessIsReaped_IsGone()
    {
        using var process = Start("/bin/sleep", "30");
        using var pidFd = OpenOrSkip(process.Id);

        process.Kill();
        process.WaitForExit();

        AssertEx.Equal(LinuxPidFdSendStatus.Gone, pidFd.Send(Sigterm, group: false).Status, "The fd names the reaped process, never whoever takes its pid next.");
        AssertEx.Equal(LinuxPidFdSendStatus.Gone, pidFd.Send(0, group: false).Status);
    }

    [Test]
    public async Task Send_ToALiveChild_DeliversTheSignal()
    {
        using var process = Start("/bin/sleep", "30");
        using var pidFd = OpenOrSkip(process.Id);

        AssertEx.Equal(LinuxPidFdSendStatus.Sent, pidFd.Send(Sigterm, group: false).Status);

        using var budget = new CancellationTokenSource(TestBudgets.Contended);
        await process.WaitForExitAsync(budget.Token);
        AssertEx.Equal(SignalledBySigterm, process.ExitCode, "SIGTERM, not some other end, stopped the child.");
    }

    /// <summary>The box kernel takes the group flag natively, so a wrong flag value cannot pass through the fallback unnoticed.</summary>
    [Test]
    public async Task GroupSend_ReachesTheLeaderAndEveryMember_ThroughTheNativeFlag()
    {
        SkipBeforeTheGroupFlag();
        using var leader = Start(Setsid, "/bin/sh", "-c", "/bin/sleep 30 & echo $!; wait");
        var member = await ReadMemberAsync(leader);
        try
        {
            using var pidFd = OpenOrSkip(leader.Id);
            AssertEx.Equal(leader.Id, LinuxTrainingProcessInspector.ReadStatOrNull(member.ProcessId)?.Pgid, "The member sits in the group the leader leads.");

            var sent = pidFd.Send(Sigterm, group: true);

            AssertEx.Equal(LinuxPidFdSendStatus.Sent, sent.Status, $"The native group flag ran (errno {sent.Errno}); a refusal would mean a filter.");
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            await leader.WaitForExitAsync(budget.Token);
            await member.SkipIfLeftUnreapedAsync();
        }
        finally
        {
            member.KillIfStillOurs();
        }
    }

    /// <summary>A group send names the group of the pinned pid, so a member still receives it after the leader is reaped.</summary>
    [Test]
    public async Task GroupSend_AfterTheLeaderIsReaped_ReachesTheSurvivingMember()
    {
        SkipBeforeTheGroupFlag();
        using var leader = Start(Setsid, "/bin/sh", "-c", "/bin/sleep 30 </dev/null >/dev/null 2>&1 & echo $!; read line");
        var member = await ReadMemberAsync(leader);
        try
        {
            using var pidFd = OpenOrSkip(leader.Id);
            await leader.StandardInput.WriteLineAsync("exit");
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            await leader.WaitForExitAsync(budget.Token);
            AssertEx.Equal(TrainingProcessPresence.Alive, LinuxTrainingProcessInspector.ReadStat(member.ProcessId).Presence, "The premise: the member outlived its reaped leader.");

            AssertEx.Equal(LinuxPidFdSendStatus.Sent, pidFd.Send(Sigterm, group: true).Status);

            await member.SkipIfLeftUnreapedAsync();
            AssertEx.Equal(LinuxPidFdSendStatus.Gone, pidFd.Send(0, group: true).Status, "Once the last member is gone, the group reads empty.");
        }
        finally
        {
            member.KillIfStillOurs();
        }
    }

    [Test]
    public void Open_SetsCloseOnExec_SoNoLaterSpawnInheritsTheFd()
    {
        const int closeOnExec = 0x80000; // O_CLOEXEC, 02000000 octal
        using var process = Start("/bin/sleep", "30");
        try
        {
            using var pidFd = OpenOrSkip(process.Id);
#pragma warning disable S3869 // The fd number is read only to find its fdinfo entry; the handle stays referenced throughout.
            var fd = pidFd.DangerousGetHandle();
#pragma warning restore S3869
            var flagsLine = File.ReadAllLines($"/proc/self/fdinfo/{fd}").Single(static line => line.StartsWith("flags:", StringComparison.Ordinal));

            var flags = Convert.ToInt32(flagsLine["flags:".Length..].Trim(), 8);

            AssertEx.True((flags & closeOnExec) != 0, $"fdinfo reports flags {flagsLine}.");
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
    }

    [Test]
    public void Send_OnADisposedFd_IsGone_AndNeverThrows()
    {
        var pidFd = OpenOrSkip(Environment.ProcessId);
        AssertEx.Equal(LinuxPidFdSendStatus.Sent, pidFd.Send(0, group: false).Status, "Signal 0 to this process only checks that it exists.");

        pidFd.Dispose();

        AssertEx.Equal(LinuxPidFdSendStatus.Gone, pidFd.Send(0, group: false).Status);
    }

    /// <summary>Signal 0 to a process another user owns sends nothing, and EPERM is an operational failure, never proof the process is gone.</summary>
    [Test]
    public void Send_WithoutPermission_IsFailedWithEperm()
    {
        const int eperm = 1;
        if (OwnerUid("1") is not { } initOwner || initOwner == OwnerUid("self"))
        {
            Skip.Test("BLOCKED: pid 1 is unreadable or runs as this user, so no signal to it is refused.");
        }

        using var pidFd = OpenOrSkip(1);

        var sent = pidFd.Send(0, group: false);

        AssertEx.Equal(new LinuxPidFdSendResult(LinuxPidFdSendStatus.Failed, eperm), sent, "The errno travels with the status it explains.");
    }

    private static Process Start(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"{fileName} did not start.");
    }

    /// <summary>PIDFD_SIGNAL_PROCESS_GROUP arrived in Linux 6.9; an older kernel refuses it, which is the fallback's case, not this native one.</summary>
    private static void SkipBeforeTheGroupFlag()
    {
        if (Environment.OSVersion.Version < new Version(6, 9))
        {
            Skip.Test($"BLOCKED: kernel {Environment.OSVersion.Version} predates PIDFD_SIGNAL_PROCESS_GROUP (Linux 6.9).");
        }
    }

    private static LinuxPidFd OpenOrSkip(int pid)
    {
        var pidFd = LinuxPidFd.Open(pid);
        if (pidFd.OpenStatus == LinuxPidFdOpenStatus.Unsupported)
        {
            pidFd.Dispose();
            Skip.Test("BLOCKED: this kernel or architecture offers no pidfd_open.");
        }

        AssertEx.Equal(LinuxPidFdOpenStatus.Opened, pidFd.OpenStatus, $"pidfd_open failed with errno {pidFd.OpenErrno}.");
        return pidFd;
    }

    /// <summary>The leader's background member, whose pid the script prints first, with its start time read at once.</summary>
    private static async Task<OwnedProcess> ReadMemberAsync(Process leader)
    {
        using var budget = new CancellationTokenSource(TestBudgets.Contended);
        var line = await leader.StandardOutput.ReadLineAsync(budget.Token) ?? throw new InvalidOperationException("The script printed no member pid.");
        var pid = int.Parse(line, CultureInfo.InvariantCulture);
        return new OwnedProcess
        {
            ProcessId = pid,
            StartTicks = LinuxTrainingProcessInspector.ReadStatOrNull(pid)?.StartTicks ?? throw new InvalidOperationException("The member exited at once.")
        };
    }

    /// <summary>The real uid in <c>/proc/[pid]/status</c>, or null when unreadable.</summary>
    private static string? OwnerUid(string pid)
    {
        try
        {
            return File.ReadLines($"/proc/{pid}/status")
                       .FirstOrDefault(static line => line.StartsWith("Uid:", StringComparison.Ordinal))?
                       .Split('\t', StringSplitOptions.RemoveEmptyEntries)[1];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
