namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    private const int Esrch = 3;
    private const int Eperm = 1;
    internal const string Setsid = "/usr/bin/setsid";
    internal const string RunToken = "token";
    private const string Python = "/opt/uv/python/bin/python3.14";

    [Test]
    public void AwaitTrainerIdentity_WhenSetsidRunsLate_WaitsForTheChildToLeadItsGroup()
    {
        var time = new ManualClock();
        var reads = 0;

        var (stat, leadsGroup, _) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            _ => new TrainingProcessStat(++reads < 4 ? HostGroup : Pid, StartTicks: 99),
            static _ => Python,
            static _ => RunToken,
            RunToken,
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
            static _ => RunToken,
            RunToken,
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
            static _ => RunToken,
            RunToken,
            Setsid,
            static _ => true,
            time);

        AssertEx.False(leadsGroup);
        AssertEx.Equal(expected: 1, reads, "An exited child is not polled again.");
    }

    [Test]
    public void AwaitTrainerIdentity_WhenStatStaysUnreadable_RetriesABoundedNumberOfTimesWithoutConfirming()
    {
        var time = new ManualClock();
        var reads = 0;

        var (stat, leadsGroup, _) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            _ =>
            {
                reads++;
                return null;
            },
            static _ => null,
            static _ => RunToken,
            RunToken,
            Setsid,
            interval => Tick(time, interval),
            time);

        AssertEx.False(leadsGroup);
        AssertEx.Null(stat);
        AssertEx.Equal(LinuxTrainingProcessSpawner.UnreadableStatRetries + 1, reads, "A live child's unreadable stat is retried, but not until the timeout.");
    }

    [Test]
    public void AwaitTrainerIdentity_WhenALaterReadFails_KeepsTheEarlierTrustedStat()
    {
        var time = new ManualClock();
        var reads = 0;

        var (stat, leadsGroup, executable) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            _ => ++reads == 1 ? new TrainingProcessStat(Pid, StartTicks: 99) : null,
            static _ => Setsid,
            static _ => RunToken,
            RunToken,
            Setsid,
            interval => Tick(time, interval),
            time);

        AssertEx.Equal(new TrainingProcessStat(Pid, StartTicks: 99), stat, "A start time read once must not be thrown away by a failed read after it.");
        AssertEx.True(leadsGroup, "Group leadership comes from the kept stat.");
        AssertEx.Null(executable);
    }

    [Test]
    public void AwaitTrainerIdentity_WhenTheChildExitsAfterARead_DiscardsThatRead()
    {
        var time = new ManualClock();
        var reads = 0;

        var (stat, leadsGroup, _) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            // The second read may describe a process that took the reaped pid.
            _ => new TrainingProcessStat(++reads == 1 ? HostGroup : Pid, StartTicks: reads == 1 ? 99 : 100),
            static _ => Python,
            static _ => RunToken,
            RunToken,
            Setsid,
            _ => reads >= 2,
            time);

        AssertEx.Equal(new TrainingProcessStat(HostGroup, StartTicks: 99), stat, "Only the read the child outlived is trusted.");
        AssertEx.False(leadsGroup, "The discarded read's group is not the child's.");
        AssertEx.Equal(expected: 2, reads);
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
            static _ => RunToken,
            RunToken,
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
            static _ => RunToken,
            RunToken,
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

        var single = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken);
        var group = await inspector.KillProcessGroupAsync(Pid, expectedStartTicks: 99, RunToken);

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

        var presence = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken);

        AssertEx.Equal(expected: 1, kills.Count, "Only the SIGTERM went to the validated process.");
        AssertEx.Equal((Pid, 15), kills[0]);
        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
    }

    /// <summary>A missing leader proves nothing while the group named by its pid has members: a trainer that ran setsid late leads it.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments(false, TrainingProcessPresence.Gone)]
    [Arguments(true, TrainingProcessPresence.Unknown)]
    public async Task KillProcess_WhenThePidIsMissing_SendsNoSignal_AndIsGoneOnlyOnceItsGroupIsEmpty(bool groupHasMembers, TrainingProcessPresence expected)
    {
        var signals = new List<SentSignal>();
        var probes = 0;
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => TrainingProcessStatRead.Missing,
            RecordingKill(signals, () =>
            {
                probes++;
                return groupHasMembers;
            }));

        var presence = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken);

        AssertEx.Empty(signals);
        AssertEx.Equal(expected: 1, probes, "A pid-only kill probes the group once and never waits on it.");
        AssertEx.Equal(expected, presence);
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

        var single = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken);
        var group = await inspector.KillProcessGroupAsync(Pid, expectedStartTicks: 99, RunToken);

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

        var presence = await DriveAsync(inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken), time);

        AssertEx.Equal("15", string.Join(',', signals), "The SIGKILL needs its own matching read.");
        AssertEx.Equal(TrainingProcessPresence.Unknown, presence);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenTheProcessSurvivesTheTerm_KillsAndVerifiesItGone()
    {
        var time = new ManualTimeProvider();
        var signals = new List<SentSignal>();
        var inspector = new LinuxTrainingProcessInspector(time,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => signals.Exists(static sent => sent.Signal == 9) ? TrainingProcessStatRead.Missing : Alive(startTicks: 99),
            RecordingKill(signals, static () => false));

        var presence = await DriveAsync(inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken), time);

        AssertEx.Equal("15,9", string.Join(',', signals.Select(static sent => sent.Signal)));
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

        var presence = await DriveAsync(inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken), time);

        AssertEx.Equal(TrainingProcessPresence.Alive, presence);
        AssertEx.True(time.GetUtcNow() - start >= LinuxTrainingProcessInspector.KillVerifyBound, "The re-inspection waits out its bound before giving up.");
    }

    /// <summary>One verify bound covers the whole kill: a leader that dies just before it leaves its surviving group only what is left, never a fresh bound.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcessGroup_WhenTheLeaderDiesJustBeforeTheVerifyBound_GivesTheGroupOnlyWhatIsLeftOfIt()
    {
        var time = new ManualTimeProvider();
        var start = time.GetUtcNow();
        var signals = new List<int>();
        DateTimeOffset? killedAt = null;
        var probes = 0;
        var inspector = new LinuxTrainingProcessInspector(time,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => killedAt is { } kill && time.GetUtcNow() >= kill + LinuxTrainingProcessInspector.KillVerifyBound - TimeSpan.FromSeconds(2)
                ? TrainingProcessStatRead.Missing
                : Alive(startTicks: 99),
            (_, signal) =>
            {
                if (signal == 0)
                {
                    probes++;
                    return 0; // A member that survives every signal.
                }

                signals.Add(signal);
                killedAt ??= signal == 9 ? time.GetUtcNow() : null;
                return 0;
            });

        var presence = await DriveAsync(inspector.KillProcessGroupAsync(Pid, expectedStartTicks: 99, RunToken), time);

        AssertEx.Equal(TrainingProcessPresence.Unknown, presence, "The group still has a member.");
        AssertEx.Equal("15,9", string.Join(',', signals));
        AssertEx.True(probes > 0, "The group was probed once the leader was gone.");
        var elapsed = time.GetUtcNow() - start;
        AssertEx.True(elapsed <= LinuxTrainingProcessInspector.TerminateGrace + LinuxTrainingProcessInspector.KillVerifyBound + TimeSpan.FromSeconds(1),
            $"The whole kill stays within one verify bound after the grace (one clock step of slack); it took {elapsed}.");
    }

    /// <summary>A zombie holds nothing itself, but it still names its group, whose members may hold the GPU.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments(false, TrainingProcessPresence.Gone)]
    [Arguments(true, TrainingProcessPresence.Unknown)]
    public async Task KillProcess_WhenTheTrainerIsAZombieAfterTheTerm_IsGoneWithoutAKill_OnceItsGroupIsEmpty(bool groupHasMembers, TrainingProcessPresence expected)
    {
        var signals = new List<SentSignal>();
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => signals.Count == 0 ? Alive(startTicks: 99) : ZombieStat(startTicks: 99),
            RecordingKill(signals, () => groupHasMembers));

        var presence = await inspector.KillProcessAsync(Pid, expectedStartTicks: 99, RunToken);

        AssertEx.Equal(new SentSignal
        {
            Target = Pid,
            Signal = 15
        }, signals.Single(), "Only the pid is signalled, and only while it is alive.");
        AssertEx.Equal(expected, presence);
    }

    [Test]
    [RunOn(OS.Linux)]
    [Arguments(0, 0, true)]
    [Arguments(-1, Esrch, false)]
    [Arguments(-1, Eperm, true)]
    public void ProcessGroupHasMembers_ReadsOnlyNoSuchGroupAsEmpty(int result, int errno, bool expected)
    {
        var calls = new List<SentSignal>();
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => throw new InvalidOperationException("A group probe reads no stat."),
            (target, signal) =>
            {
                calls.Add(new SentSignal
                {
                    Target = target,
                    Signal = signal
                });
                Marshal.SetLastPInvokeError(errno);
                return result;
            });

        AssertEx.Equal(expected, inspector.ProcessGroupHasMembers(Pid), "A refusal other than ESRCH may hide a member, so it fails closed.");
        AssertEx.Equal(new SentSignal
        {
            Target = -Pid,
            Signal = 0
        }, calls.Single(), "Signal 0 to the negated group: a probe that sends nothing.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public void ProcessGroupHasMembers_ForAGroupNoProbeCanProveEmpty_IsTrueWithoutCallingKill()
    {
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => throw new InvalidOperationException("A group probe reads no stat."),
            static (_, _) => throw new InvalidOperationException("No probe may target init's, the caller's or the host's group."));

        foreach (var group in new[]
                 {
                     -5,
                     0,
                     1,
                     LinuxTrainingProcessInspector.HostProcessGroupId
                 })
        {
            AssertEx.True(inspector.ProcessGroupHasMembers(group), $"Group {group} can never be proven empty.");
        }
    }

    [Test]
    [ExcludeOn(OS.Linux)]
    public void ProcessGroupHasMembers_OffLinux_IsFalseWithoutCallingKill()
    {
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            static _ => throw new InvalidOperationException("Nothing is read off Linux."),
            static (_, _) => throw new InvalidOperationException("Nothing is signalled off Linux."));

        AssertEx.False(inspector.ProcessGroupHasMembers(Pid), "Off Linux a missing pid already reads gone; the probe keeps that policy.");
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

    /// <summary>A grandchild that left the group (setsid) may hold the output pipe; the exit-only wait still sees the trainer's exit.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task WaitForGroupExit_WhenAGrandchildOutsideTheGroupHoldsThePipe_ReturnsTheExitCode()
    {
        if (!File.Exists(Setsid))
        {
            Skip.Test($"BLOCKED: {Setsid} is absent, so the script cannot move its grandchild out of the group.");
        }

        var root = Path.Combine(Path.GetTempPath(), "xe-spawn-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        OwnedProcess? grandchild = null;
        try
        {
            var spawner = new LinuxTrainingProcessSpawner(TimeProvider.System, NullLogger<LinuxTrainingProcessSpawner>.Instance, root);
            using var handle = spawner.Spawn(new TrainingSpawnRequest
            {
                ExecutablePath = "/bin/sh",
                Arguments = ["-c", ExitOnInput("/usr/bin/setsid /bin/sleep 30 &")],
                WorkingDirectory = root,
                RunToken = "token",
                RedirectStandardInput = true
            });
            AssertEx.Equal(handle.Receipt.Pid, handle.Receipt.Pgid, "The trainer leads its group, so the group is checked too.");
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            grandchild = await ReadChildAsync(handle, budget.Token);
            await handle.WriteLineAsync("exit", budget.Token);

            var exitCode = await handle.WaitForGroupExitAsync(budget.Token);

            AssertEx.Equal<int?>(0, exitCode);
            AssertEx.Equal(TrainingProcessPresence.Alive, LinuxTrainingProcessInspector.ReadStat(grandchild.ProcessId).Presence, "The grandchild still holds the pipe.");
        }
        finally
        {
            grandchild?.KillIfStillOurs();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A child left in the trainer's group may still hold the GPU: the trainer's exit alone is not a verified exit.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task WaitForGroupExit_WhileAChildInTheGroupOutlivesTheTrainer_IsNotVerified_UntilTheChildIsGone()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-spawn-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        OwnedProcess? child = null;
        try
        {
            var spawner = new LinuxTrainingProcessSpawner(TimeProvider.System, NullLogger<LinuxTrainingProcessSpawner>.Instance, root);
            using var handle = spawner.Spawn(new TrainingSpawnRequest
            {
                ExecutablePath = "/bin/sh",
                // The child ignores SIGTERM, so the group's cooperative stop would not end it either.
                Arguments = ["-c", "trap '' TERM; " + ExitOnInput("/bin/sleep 30 &")],
                WorkingDirectory = root,
                RunToken = "token",
                RedirectStandardInput = true
            });
            AssertEx.Equal(handle.Receipt.Pid, handle.Receipt.Pgid, "The trainer leads its group, so the group is checked too.");
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            child = await ReadChildAsync(handle, budget.Token);
            await handle.WriteLineAsync("exit", budget.Token);
            AssertEx.Equal(handle.Receipt.Pgid, LinuxTrainingProcessInspector.ReadStatOrNull(child.ProcessId)?.Pgid, "The child shares the trainer's group.");
            await AssertEx.EventuallyAsync(() => LinuxTrainingProcessInspector.ReadStat(handle.Receipt.Pid).Presence == TrainingProcessPresence.Gone,
                TestBudgets.Contended,
                "The trainer itself exits at once.");
            using var bound = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            var exitCode = await handle.WaitForGroupExitAsync(bound.Token);

            AssertEx.Null(exitCode, "A group with a live member stays unverified for the whole bound.");

            // Positive control: once the last member is gone, the same wait verifies the exit.
            child.KillIfStillOurs();
            await child.SkipIfLeftUnreapedAsync();
            using var verified = new CancellationTokenSource(TestBudgets.Contended);
            AssertEx.Equal<int?>(0, await handle.WaitForGroupExitAsync(verified.Token), "The emptied group verifies the trainer's exit.");
        }
        finally
        {
            child?.KillIfStillOurs();
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

    /// <summary>Starts <paramref name="background" />, prints its pid, and exits 0 on the first stdin line: the spawn confirms the trainer's group before it exits.</summary>
    private static string ExitOnInput(string background) =>
        background + " echo $!; read line; exit 0";

    /// <summary>The background child whose pid the test's shell script prints first, with its start time read at once.</summary>
    internal static async Task<OwnedProcess> ReadChildAsync(ITrainingProcessHandle handle, CancellationToken ct)
    {
        await using var lines = handle.ReadOutputAsync(ct).GetAsyncEnumerator(ct);
        AssertEx.True(await lines.MoveNextAsync(), "The script prints its child's pid.");
        var pid = int.Parse(lines.Current, CultureInfo.InvariantCulture);
        var stat = LinuxTrainingProcessInspector.ReadStatOrNull(pid)
                   ?? throw new InvalidOperationException("The child exited before its start time could be read.");
        return new OwnedProcess
        {
            ProcessId = pid,
            StartTicks = stat.StartTicks
        };
    }

    private static bool Tick(ManualClock time, TimeSpan interval)
    {
        time.Advance(interval);
        return false;
    }

    /// <summary><c>kill(2)</c> that records every real signal and answers each signal-0 group probe: a member while <paramref name="groupHasMembers" /> says so, else ESRCH.</summary>
    internal static Func<int, int, int> RecordingKill(List<SentSignal> signals, Func<bool> groupHasMembers) =>
        (target, signal) =>
        {
            if (signal != 0)
            {
                signals.Add(new SentSignal
                {
                    Target = target,
                    Signal = signal
                });
                return 0;
            }

            if (groupHasMembers())
            {
                return 0;
            }

            Marshal.SetLastPInvokeError(Esrch);
            return -1;
        };

    internal static TrainingProcessStatRead Alive(long startTicks) =>
        new()
        {
            Presence = TrainingProcessPresence.Alive,
            Stat = new TrainingProcessStat(Pid, startTicks)
        };

    internal static TrainingProcessStatRead ZombieStat(long startTicks) =>
        new()
        {
            Presence = TrainingProcessPresence.Gone,
            Stat = new TrainingProcessStat(Pid, startTicks)
        };

    /// <summary>Moves the clock past each poll the kill arms until it settles, so its bounds pass without real waiting.</summary>
    internal static async Task<T> DriveAsync<T>(Task<T> task, ManualTimeProvider time)
    {
        while (!task.IsCompleted)
        {
            await AssertEx.EventuallyAsync(() => task.IsCompleted || time.ArmedTimerCount > 0, TestBudgets.Contended, "The kill neither settled nor armed its next poll.");
            time.Advance(TimeSpan.FromSeconds(1));
        }

        return await task;
    }

    /// <summary>One <c>kill(2)</c> call: the pid, or the negated group, and the signal.</summary>
    internal sealed record SentSignal
    {
        public required int Target { get; init; }

        public required int Signal { get; init; }
    }

    /// <summary>A process a test started, known by pid and start time, so cleanup never signals a pid another process now holds.</summary>
    internal sealed class OwnedProcess
    {
        private static readonly TimeSpan ReapBound = TimeSpan.FromSeconds(10);

        public required int ProcessId { get; init; }

        public required long StartTicks { get; init; }

        /// <summary>SIGKILL only while the pid still carries the recorded start time, as the product's own signals are guarded.</summary>
        public void KillIfStillOurs()
        {
            if (!LinuxTrainingProcessInspector.MaySignal(LinuxTrainingProcessInspector.ReadStat(ProcessId), StartTicks))
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(ProcessId);
                process.Kill();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                // Gone in between.
            }
        }

        /// <summary>
        ///     After <see cref="KillIfStillOurs" />, skips the test while the process stays an unreaped zombie: it was orphaned when the
        ///     trainer exited, so only pid 1 or a subreaper can reap it, and until then it still counts as a member of its group.
        /// </summary>
        public async Task SkipIfLeftUnreapedAsync()
        {
            try
            {
                await AssertEx.EventuallyAsync(() => !LinuxTrainingProcessInspector.MaySignal(LinuxTrainingProcessInspector.ReadStat(ProcessId), StartTicks),
                    ReapBound,
                    $"Killed pid {ProcessId} is still alive.");
            }
            catch (AssertionException)
            {
                // One read decides: the reaper may run between the poll's last check and this one.
                var read = LinuxTrainingProcessInspector.ReadStat(ProcessId);
                if (!LinuxTrainingProcessInspector.MaySignal(read, StartTicks))
                {
                    return; // Reaped after all: missing, or the pid already names another process.
                }

                if (read.Presence == TrainingProcessPresence.Gone)
                {
                    Skip.Test($"BLOCKED: nothing reaps orphaned children on this host; killed pid {ProcessId} stayed a zombie in its group for {ReapBound}.");
                }

                throw;
            }
        }
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
