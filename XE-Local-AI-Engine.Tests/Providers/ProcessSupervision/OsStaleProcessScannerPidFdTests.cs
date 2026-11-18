namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The spawn-receipt kill over a pidfd: identity is read after the open, the signal goes through the fd, and a failed call defers.</summary>
/// <remarks>Every pidfd call, <c>/proc</c> read and <c>kill(2)</c> is scripted and logged in order; nothing reaches the kernel.</remarks>
[Category(TestCategories.Unit)]
public sealed class OsStaleProcessScannerPidFdTests
{
    private const int Pid = 5001;
    private const long StartTicks = 777;
    private const string Server = "/opt/byo/llama-server";

    [Test]
    [Arguments(Pid, StaleProcessKillResult.Killed, "open 5001,send 9 group")]
    [Arguments(6000, StaleProcessKillResult.Killed, "open 5001,send 9")]
    public void AMatchAfterTheOpen_KillsThroughTheFd_TheGroupOnlyWhenThePidLeadsIt(int processGroupId, StaleProcessKillResult expected, string calls)
    {
        var fake = new FakeCalls
        {
            Stat = new ProcessStat(ParentProcessId: 1, processGroupId, StartTicks)
        };

        AssertEx.Equal(expected, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal(calls, string.Join(',', fake.Calls));
    }

    [Test]
    [Arguments("start time")]
    [Arguments("executable")]
    public void AMismatchAfterTheOpen_SendsNothing(string mismatch)
    {
        var fake = new FakeCalls
        {
            Stat = new ProcessStat(ParentProcessId: 1, Pid, mismatch == "start time" ? StartTicks + 1 : StartTicks),
            ExecutablePath = mismatch == "executable" ? "/usr/bin/bash" : Server
        };

        AssertEx.Equal(StaleProcessKillResult.Mismatch, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks), "The fd names a recycled pid.");
        AssertEx.Equal("open 5001", string.Join(',', fake.Calls));
    }

    /// <summary>A failed read while the fd's process lives proves nothing; once the fd reads it reaped, the receipt names no process.</summary>
    [Test]
    [Arguments("stat", LinuxPidFdSendStatus.Sent, StaleProcessKillResult.Unreadable)]
    [Arguments("stat", LinuxPidFdSendStatus.Gone, StaleProcessKillResult.Mismatch)]
    [Arguments("executable", LinuxPidFdSendStatus.Sent, StaleProcessKillResult.Unreadable)]
    [Arguments("executable", LinuxPidFdSendStatus.Failed, StaleProcessKillResult.Deferred)]
    public void AnUnreadableIdentityAfterTheOpen_ProbesThroughTheFd_AndNeverKills(string unreadable, LinuxPidFdSendStatus probe, StaleProcessKillResult expected)
    {
        var fake = new FakeCalls
        {
            Stat = unreadable == "stat" ? null : new ProcessStat(ParentProcessId: 1, Pid, StartTicks),
            ExecutablePath = unreadable == "executable" ? null : Server,
            SendResult = (_, _) => probe
        };

        AssertEx.Equal(expected, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal("open 5001,send 0", string.Join(',', fake.Calls));
    }

    [Test]
    [Arguments(LinuxPidFdOpenStatus.Gone, StaleProcessKillResult.Mismatch)]
    [Arguments(LinuxPidFdOpenStatus.Failed, StaleProcessKillResult.Deferred)]
    public void AnOpenThatFindsNoProcessOrFails_ReadsAndSignalsNothing(LinuxPidFdOpenStatus open, StaleProcessKillResult expected)
    {
        var fake = new FakeCalls
        {
            OpenStatus = open
        };

        AssertEx.Equal(expected, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal("open 5001", string.Join(',', fake.Calls), "EPERM or EMFILE never falls back to the unverified numeric kill.");
    }

    [Test]
    [Arguments(LinuxPidFdSendStatus.Gone, StaleProcessKillResult.Mismatch)]
    [Arguments(LinuxPidFdSendStatus.Failed, StaleProcessKillResult.Deferred)]
    public void ASendThatFindsNoProcessOrFails_SendsNoNumericSignal(LinuxPidFdSendStatus send, StaleProcessKillResult expected)
    {
        var fake = new FakeCalls
        {
            SendResult = (_, _) => send
        };

        AssertEx.Equal(expected, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal("open 5001,send 9 group", string.Join(',', fake.Calls));
    }

    /// <summary>The numeric kill decides the outcome: only ESRCH proves the group empty, and a refusal (EPERM) keeps the receipt.</summary>
    [Test]
    [Arguments(0, StaleProcessKillResult.Killed)]
    [Arguments(3, StaleProcessKillResult.Mismatch)]
    [Arguments(1, StaleProcessKillResult.Deferred)]
    public void AGroupFlagTheKernelRefuses_ProbesThroughTheFd_ThenKillsTheGroupByNumber(int killErrno, StaleProcessKillResult expected)
    {
        var fake = new FakeCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent,
            KillErrno = killErrno
        };

        AssertEx.Equal(expected, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal("open 5001,send 9 group,send 0,kill -5001 9", string.Join(',', fake.Calls));
    }

    /// <summary>The parent is read after the open, so this host's own server is never signalled even when a preliminary read failed.</summary>
    [Test]
    [Arguments(LinuxPidFdOpenStatus.Opened)]
    [Arguments(LinuxPidFdOpenStatus.Unsupported)]
    public void ThisHostsOwnChild_IsNeverSignalled(LinuxPidFdOpenStatus open)
    {
        var fake = new FakeCalls
        {
            OpenStatus = open,
            Stat = new ProcessStat(Environment.ProcessId, Pid, StartTicks)
        };

        AssertEx.Equal(StaleProcessKillResult.OwnChild, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal("open 5001", string.Join(',', fake.Calls));
    }

    /// <summary>A zombie runs nothing and its exe link reads nothing: its receipt names no server, so it is dropped instead of kept for ever.</summary>
    [Test]
    public void AZombie_IsAMismatch_AndNothingIsSent()
    {
        var fake = new FakeCalls
        {
            Stat = new ProcessStat(ParentProcessId: 1, Pid, StartTicks, Exited: true),
            ExecutablePath = null
        };

        AssertEx.Equal(StaleProcessKillResult.Mismatch, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal("open 5001", string.Join(',', fake.Calls));
    }

    /// <summary>Where the kernel offers no pidfd, the reads, the target and the outcome stay what the receipt reap always had.</summary>
    [Test]
    [Arguments(StartTicks, Server, StaleProcessKillResult.Killed, "open 5001,kill -5001 9")]
    [Arguments(StartTicks + 1, Server, StaleProcessKillResult.Mismatch, "open 5001")]
    [Arguments(StartTicks, "/usr/bin/bash", StaleProcessKillResult.Mismatch, "open 5001")]
    public void WithoutPidFd_KeepsTheNumericCheckThenKill(long liveStartTicks, string liveExecutable, StaleProcessKillResult expected, string calls)
    {
        var fake = new FakeCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Unsupported,
            Stat = new ProcessStat(ParentProcessId: 1, Pid, liveStartTicks),
            ExecutablePath = liveExecutable
        };

        AssertEx.Equal(expected, fake.Scanner().KillIfSameProcess(Pid, Server, StartTicks));
        AssertEx.Equal(calls, string.Join(',', fake.Calls));
    }

    /// <summary>Scripted pidfd calls, <c>/proc</c> reads and <c>kill(2)</c>; opens, sends and kills are logged in order.</summary>
    private sealed class FakeCalls
    {
        public List<string> Calls { get; } = [];

        public LinuxPidFdOpenStatus OpenStatus { get; init; } = LinuxPidFdOpenStatus.Opened;

        public Func<int, bool, LinuxPidFdSendStatus> SendResult { get; init; } = static (_, _) => LinuxPidFdSendStatus.Sent;

        public ProcessStat? Stat { get; init; } = new ProcessStat(ParentProcessId: 1, Pid, StartTicks);

        public string? ExecutablePath { get; init; } = Server;

        /// <summary>What the numeric <c>kill(2)</c> fails with; 0, accepted, by default.</summary>
        public int KillErrno { get; init; }

        public OsStaleProcessScanner Scanner() =>
            new("llama-server",
                pid =>
                {
                    Calls.Add($"open {pid}");
                    return new LinuxPidFd(preexistingHandle: OpenStatus == LinuxPidFdOpenStatus.Opened ? 4096 : -1, ownsHandle: false, OpenStatus);
                },
                (_, signal, group) =>
                {
                    Calls.Add(group ? $"send {signal} group" : $"send {signal}");
                    return new LinuxPidFdSendResult(SendResult(signal, group), Errno: 0);
                },
                _ => Stat,
                _ => ExecutablePath,
                (target, signal) =>
                {
                    Calls.Add($"kill {target} {signal}");
                    return KillErrno;
                });
    }
}
