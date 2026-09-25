namespace XE_Local_AI_Engine.Tests.Providers.Training;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     A trainer is signalled through its process group, so a receipt that recorded the HOST's group turned an operator
///     cancel into a SIGTERM to the node, its launcher and Vite. These pin the confirmation loop and the guard.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LinuxTrainingProcessGroupTests
{
    private const int Pid = 4242;
    private const int HostGroup = 777;
    private const string Setsid = "/usr/bin/setsid";
    private const string Python = "/opt/uv/python/bin/python3.13";

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
            static _ => new TrainingProcessStat(Pid, StartTicks: 100),
            (target, signal) =>
            {
                kills.Add((target, signal));
                return 0;
            });

        await inspector.KillProcessAsync(Pid, expectedStartTicks: 99);
        await inspector.KillProcessGroupAsync(Pid, expectedStartTicks: 99);

        AssertEx.Empty(kills, "A pid whose start time changed belongs to a stranger now.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillProcess_WhenThePidIsRecycledAfterTheTerm_SkipsTheKill()
    {
        var kills = new List<(int Target, int Signal)>();
        var reads = 0;
        var inspector = new LinuxTrainingProcessInspector(TimeProvider.System,
            NullLogger<LinuxTrainingProcessInspector>.Instance,
            _ => new TrainingProcessStat(Pid, StartTicks: reads++ == 0 ? 99 : 100),
            (target, signal) =>
            {
                kills.Add((target, signal));
                return 0;
            });

        await inspector.KillProcessAsync(Pid, expectedStartTicks: 99);

        AssertEx.Equal(expected: 1, kills.Count, "Only the SIGTERM went to the validated process.");
        AssertEx.Equal((Pid, 15), kills[0]);
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
        AssertEx.Equal(LinuxTrainingProcessInspector.TryReadStat(Environment.ProcessId)?.Pgid, LinuxTrainingProcessInspector.HostProcessGroupId);

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

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() =>
            _now;

        public void Advance(TimeSpan interval) =>
            _now += interval;
    }
}
