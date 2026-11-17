namespace XE_Local_AI_Engine.Tests.Providers.Training;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     A trainer is signaled through its process group, so a receipt that recorded the HOST's group turned an operator
///     cancel into a SIGTERM to the node, its launcher and Vite. These pin the confirmation loop and the guard.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LinuxTrainingProcessGroupTests
{
    private const int Pid = 4242;
    private const int HostGroup = 777;
    private const string Setsid = "/usr/bin/setsid";
    private const string Python = "/opt/uv/python/bin/python3.14";

    [Test]
    public void AwaitTrainerIdentity_WhenSetsidRunsLate_WaitsForTheChildToLeadItsGroup()
    {
        var time = new ManualClock();
        var reads = 0;

        var (stat, leadsGroup, _) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            _ => new TrainingProcessStat(++reads < 4 ? HostGroup : Pid, StartTicks: 99),
            static _ => Python,
            Setsid,
            interval => Tick(time, interval),
            time);

        AssertEx.True(leadsGroup, "The child became its own group leader on the fourth read.");
        AssertEx.Equal(Pid, stat?.Pgid);
        AssertEx.Equal(expected: 4, reads);
    }

    [Test]
    public void AwaitTrainerIdentity_WhenTheChildNeverLeadsAGroup_GivesUpAtTheTimeoutWithoutConfirming()
    {
        var time = new ManualClock();
        var start = time.GetUtcNow();

        var (stat, leadsGroup, _) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            static _ => new TrainingProcessStat(HostGroup, StartTicks: 99),
            static _ => Python,
            Setsid,
            interval => Tick(time, interval),
            time);

        AssertEx.False(leadsGroup, "A child still in the host's group must never be recorded as a group leader.");
        AssertEx.Equal(HostGroup, stat?.Pgid);
        AssertEx.True(time.GetUtcNow() - start >= LinuxTrainingProcessSpawner.GroupLeaderTimeout, "The loop is bounded by the timeout.");
    }

    [Test]
    public void AwaitTrainerIdentity_WhenTheChildExits_StopsWithoutConfirming()
    {
        var time = new ManualClock();
        var reads = 0;

        var (_, leadsGroup, _) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            _ =>
            {
                reads++;
                return new TrainingProcessStat(HostGroup, StartTicks: 99);
            },
            static _ => Python,
            Setsid,
            static _ => true,
            time);

        AssertEx.False(leadsGroup);
        AssertEx.Equal(expected: 1, reads, "An exited child is not polled again.");
    }

    [Test]
    public void AwaitTrainerIdentity_WhenStatIsUnreadable_StopsWithoutConfirming()
    {
        var (stat, leadsGroup, _) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            static _ => null,
            static _ => null,
            Setsid,
            static _ => throw new InvalidOperationException("An unreadable stat must not be waited on."),
            new ManualClock());

        AssertEx.False(leadsGroup);
        AssertEx.Null(stat);
    }

    [Test]
    public void AwaitTrainerIdentity_WhenTheChildLeadsItsGroupBeforeExecingTheTrainer_WaitsForTheTrainerExecutable()
    {
        var time = new ManualClock();
        (int Pgid, string Exe)[] sequence = [(HostGroup, Setsid), (Pid, Setsid), (Pid, Python)];
        var reads = 0;

        var (stat, leadsGroup, executable) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            _ => new TrainingProcessStat(sequence[Math.Min(reads++, sequence.Length - 1)].Pgid, StartTicks: 99),
            _ => sequence[Math.Min(reads - 1, sequence.Length - 1)].Exe,
            Setsid,
            interval => Tick(time, interval),
            time);

        AssertEx.True(leadsGroup);
        AssertEx.Equal(Pid, stat?.Pgid);
        AssertEx.Equal(Python, executable, "A receipt taken while setsid still owned the pid would record the launcher's path.");
        AssertEx.Equal(expected: 3, reads);
    }

    [Test]
    public void AwaitTrainerIdentity_WhenTheExecNeverHappensInTime_RecordsNoExecutablePath()
    {
        var time = new ManualClock();

        var (stat, leadsGroup, executable) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            static _ => new TrainingProcessStat(Pid, StartTicks: 99),
            static _ => Setsid,
            Setsid,
            interval => Tick(time, interval),
            time);

        AssertEx.True(leadsGroup);
        AssertEx.Equal<long?>(99, stat?.StartTicks);
        AssertEx.Null(executable, "A timed-out receipt must not pin setsid's path; the trainer execs a moment later.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenThePidWasRecycledAfterInspection_SendsNoSignal()
    {
        var kills = new List<(int Target, int Signal)>();
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => Alive(startTicks: 100),
            (target, signal) =>
            {
                kills.Add((target, signal));
                return 0;
            });

        var single = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99);
        var group = await inspector.KillProcessGroupAsync(Pid, expectedStartTicks: 99);

        AssertEx.Empty(kills, "A pid whose start time changed belongs to a stranger now.");
        AssertEx.Equal(TrainingProcessPresence.Gone, single, "The recorded process itself is gone.");
        AssertEx.Equal(TrainingProcessPresence.Gone, group);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenThePidIsRecycledAfterTheTerm_SkipsTheKill()
    {
        var kills = new List<(int Target, int Signal)>();
        var reads = 0;
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => Alive(startTicks: reads++ == 0 ? 99 : 100),
            (target, signal) =>
            {
                kills.Add((target, signal));
                return 0;
            });

        var presence = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99);

        AssertEx.Equal(expected: 1, kills.Count, "Only the SIGTERM went to the validated process.");
        AssertEx.Equal((Pid, 15), kills[0]);
        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenThePidIsMissing_SendsNoSignalAndIsGone()
    {
        var kills = 0;
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => TrainingProcessStatRead.Missing,
            (_, _) => ++kills);

        var presence = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99);

        AssertEx.Equal(expected: 0, kills);
        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenTheStatIsUnreadable_SendsNoSignalAndNeverReportsGone()
    {
        var signals = new List<int>();
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => TrainingProcessStatRead.Unreadable,
            (_, signal) =>
            {
                signals.Add(signal);
                return 0;
            });

        var single = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99);
        var group = await inspector.KillProcessGroupAsync(Pid, expectedStartTicks: 99);

        AssertEx.Empty(signals, "An unreadable entry may be a reused pid: only a stat read now and matching may be signalled.");
        AssertEx.Equal(TrainingProcessPresence.Unknown, single, "Unreadable is never reported as gone.");
        AssertEx.Equal(TrainingProcessPresence.Unknown, group);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenTheStatTurnsUnreadableAfterTheTerm_SkipsTheKillAndIsUnknown()
    {
        var time = new ManualTimeProvider();
        var signals = new List<int>();
        var inspector = new LinuxTrainingProcessInspector(time,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => signals.Count == 0 ? Alive(startTicks: 99) : TrainingProcessStatRead.Unreadable,
            (_, signal) =>
            {
                signals.Add(signal);
                return 0;
            });

        var presence = await DriveAsync(inspector.KillProcessAsync(Pid, expectedStartTicks: 99), time);

        AssertEx.Equal("15", string.Join(',', signals), "The SIGKILL needs its own matching read.");
        AssertEx.Equal(TrainingProcessPresence.Unknown, presence);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenTheProcessSurvivesTheTerm_KillsAndVerifiesItGone()
    {
        var time = new ManualTimeProvider();
        var signals = new List<int>();
        var inspector = new LinuxTrainingProcessInspector(time,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => signals.Contains(9) ? TrainingProcessStatRead.Missing : Alive(startTicks: 99),
            (_, signal) =>
            {
                signals.Add(signal);
                return 0;
            });

        var presence = await DriveAsync(inspector.KillProcessAsync(Pid, expectedStartTicks: 99), time);

        AssertEx.Equal("15,9", string.Join(',', signals));
        AssertEx.Equal(TrainingProcessPresence.Gone, presence, "The post-kill re-inspection saw the pid vanish.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenTheProcessOutlivesTheKill_GivesUpAtTheBoundAndReportsAlive()
    {
        var time = new ManualTimeProvider();
        var start = time.GetUtcNow();
        var inspector = new LinuxTrainingProcessInspector(time,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => Alive(startTicks: 99),
            static (_, _) => 0);

        var presence = await DriveAsync(inspector.KillProcessAsync(Pid, expectedStartTicks: 99), time);

        AssertEx.Equal(TrainingProcessPresence.Alive, presence);
        AssertEx.True(time.GetUtcNow() - start >= LinuxTrainingProcessInspector.KillVerifyBound, "The re-inspection waits out its bound before giving up.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenTheTrainerIsAZombieAfterTheTerm_IsGoneWithoutAKill()
    {
        var signals = new List<int>();
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => signals.Count == 0 ? Alive(startTicks: 99) : ZombieStat(startTicks: 99),
            (_, signal) =>
            {
                signals.Add(signal);
                return 0;
            });

        var presence = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99);

        AssertEx.Equal("15", string.Join(',', signals), "A zombie holds nothing; only its parent's wait() is left.");
        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
    }

    [Test]
    [Arguments("S", TrainingProcessPresence.Alive)]
    [Arguments("R", TrainingProcessPresence.Alive)]
    [Arguments("D", TrainingProcessPresence.Alive)]
    [Arguments("Z", TrainingProcessPresence.Gone)]
    [Arguments("X", TrainingProcessPresence.Gone)]
    public void ReadStatFile_ReadsPresenceFromTheStateField(string state, TrainingProcessPresence expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "xe-stat-" + Guid.NewGuid().ToString("N"));
        // comm may hold spaces and parentheses; fields after it: state, ppid, pgrp, 16 more, then starttime.
        File.WriteAllText(path, $"{Pid} (py (x) y) {state} 1 {Pid} {string.Join(' ', Enumerable.Repeat("0", 16))} 99 0 0\n");
        try
        {
            var read = LinuxTrainingProcessInspector.ReadStatFile(path);

            AssertEx.Equal(expected, read.Presence);
            AssertEx.Equal(new TrainingProcessStat(Pid, StartTicks: 99), read.Stat, "A zombie keeps its fields: its group may still hold live workers.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [Arguments("")]
    [Arguments("garbage without a comm")]
    [Arguments("4242 (py) S 1 4242")]
    [Arguments("4242 (py) S 1 notanumber 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 99")]
    public void ReadStatFile_WhenTheContentDoesNotParse_IsUnknownNeverGone(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "xe-stat-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        try
        {
            var read = LinuxTrainingProcessInspector.ReadStatFile(path);

            AssertEx.Equal(TrainingProcessPresence.Unknown, read.Presence);
            AssertEx.Null(read.Stat);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    public void ReadStatFile_WhenTheEntryCannotBeRead_IsUnknown()
    {
        // Reading a directory as a file fails with an access error on Linux whatever the user's privileges.
        var directory = Directory.CreateTempSubdirectory("xe-stat-");
        try
        {
            AssertEx.Equal(TrainingProcessPresence.Unknown, LinuxTrainingProcessInspector.ReadStatFile(directory.FullName).Presence);
        }
        finally
        {
            directory.Delete();
        }
    }

    [Test]
    public void ReadStatFile_WhenTheFileOrItsDirectoryIsMissing_IsGone()
    {
        var missing = Path.Combine(Path.GetTempPath(), "xe-stat-" + Guid.NewGuid().ToString("N"));

        AssertEx.Equal(TrainingProcessPresence.Gone, LinuxTrainingProcessInspector.ReadStatFile(missing).Presence);
        AssertEx.Equal(TrainingProcessPresence.Gone, LinuxTrainingProcessInspector.ReadStatFile(Path.Combine(missing, "stat")).Presence);
    }

    [Test]
    [RunOn(OS.Linux)]
    public void ReadStat_ReportsTheLiveHostAliveAndAPidBeyondPidMaxGone()
    {
        AssertEx.Equal(TrainingProcessPresence.Alive, LinuxTrainingProcessInspector.ReadStat(Environment.ProcessId).Presence);
        AssertEx.Equal(TrainingProcessPresence.Gone, LinuxTrainingProcessInspector.ReadStat(int.MaxValue).Presence, "pid_max is far below int.MaxValue.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task WriteLine_ReachesTheChildsStdinAsUtf8WithoutABom()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-spawn-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var spawner = new LinuxTrainingProcessSpawner(TimeProvider.System, NullLogger<LinuxTrainingProcessSpawner>.Instance, root);
            using var handle = spawner.Spawn(new TrainingSpawnRequest
            {
                ExecutablePath = "/bin/cat",
                Arguments = [],
                WorkingDirectory = root,
                RunToken = "token",
                RedirectStandardInput = true
            });
            const string command = "{\"op\":\"run\",\"jobId\":\"grüße\"}";

            await handle.WriteLineAsync(command, CancellationToken.None);

            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            await using var lines = handle.ReadOutputAsync(budget.Token).GetAsyncEnumerator(budget.Token);
            AssertEx.True(await lines.MoveNextAsync(), "cat echoes the written line back on stdout.");
            AssertEx.Equal(command, lines.Current, "A BOM or a non-UTF-8 encoding would change the echoed bytes.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task WriteLine_WhenStdinWasNotRequested_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-spawn-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var spawner = new LinuxTrainingProcessSpawner(TimeProvider.System, NullLogger<LinuxTrainingProcessSpawner>.Instance, root);
            using var handle = spawner.Spawn(new TrainingSpawnRequest
            {
                ExecutablePath = "/bin/sleep",
                Arguments = ["30"],
                WorkingDirectory = root,
                RunToken = "token"
            });

            _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => handle.WriteLineAsync("{}", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Arguments(Pid, Pid, HostGroup, true)]
    [Arguments(Pid, HostGroup, HostGroup, false)]
    [Arguments(HostGroup, HostGroup, HostGroup, false)]
    [Arguments(Pid, 5000, HostGroup, false)]
    [Arguments(Pid, 0, HostGroup, false)]
    [Arguments(1, 1, HostGroup, false)]
    public void MaySignalGroup_AllowsOnlyAGroupTheTrainerLeadsThatIsNotTheHosts(int pid, int pgid, int host, bool expected) =>
        AssertEx.Equal(expected, TrainingProcessGroupGuard.MaySignalGroup(pid, pgid, host));

    [Test]
    [RunOn(OS.Linux)]
    public void HostProcessGroupId_MatchesTheHostsOwnProcStat() =>
        AssertEx.Equal(LinuxTrainingProcessInspector.ReadStatOrNull(Environment.ProcessId)?.Pgid, LinuxTrainingProcessInspector.HostProcessGroupId);

    [Test]
    [RunOn(OS.Linux)]
    public void Spawn_RecordsAGroupTheTrainerLeadsNeverTheHosts()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-spawn-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            var spawner = new LinuxTrainingProcessSpawner(TimeProvider.System, NullLogger<LinuxTrainingProcessSpawner>.Instance, root);
            using var handle = spawner.Spawn(new TrainingSpawnRequest
            {
                ExecutablePath = "/bin/sleep",
                Arguments = ["30"],
                WorkingDirectory = root,
                RunToken = "token"
            });

            AssertEx.Equal(handle.Receipt.Pid, handle.Receipt.Pgid, "setsid must have made the trainer its own group leader.");
            AssertEx.NotEqual(LinuxTrainingProcessInspector.HostProcessGroupId, handle.Receipt.Pgid);
            AssertEx.True(handle.Receipt.ExecutablePath?.EndsWith("/sleep", StringComparison.Ordinal) == true,
                $"The receipt must record the trainer's executable, not setsid's; got '{handle.Receipt.ExecutablePath}'.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool Tick(ManualClock time, TimeSpan interval)
    {
        time.Advance(interval);
        return false;
    }

    private static TrainingProcessStatRead Alive(long startTicks) =>
        new()
        {
            Presence = TrainingProcessPresence.Alive,
            Stat = new TrainingProcessStat(Pid, startTicks)
        };

    private static TrainingProcessStatRead ZombieStat(long startTicks) =>
        new()
        {
            Presence = TrainingProcessPresence.Gone,
            Stat = new TrainingProcessStat(Pid, startTicks)
        };

    /// <summary>Moves the clock past each poll the kill arms until it settles, so its bounds pass without real waiting.</summary>
    private static async Task<T> DriveAsync<T>(Task<T> task, ManualTimeProvider time)
    {
        while (!task.IsCompleted)
        {
            await AssertEx.EventuallyAsync(() => task.IsCompleted || time.ArmedTimerCount > 0, TestBudgets.Contended, "The kill neither settled nor armed its next poll.");
            time.Advance(TimeSpan.FromSeconds(1));
        }

        return await task;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() =>
            _now;

        public void Advance(TimeSpan interval) =>
            _now += interval;
    }
}
