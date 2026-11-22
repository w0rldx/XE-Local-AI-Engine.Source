namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using static XE_Local_AI_Engine.Tests.Providers.Training.LinuxTrainingProcessGroupTests;

/// <summary>The sandbox group kill matrix, foreign (a dead host's marker) and owned (this host's child), over scripted pidfd and kill calls.</summary>
/// <remarks>Nothing reaches the kernel. A numeric signal other than the signal-0 probe is sent only while the leader is proven alive.</remarks>
[Category(TestCategories.Unit)]
public sealed class LinuxSandboxProcessGroupKillerTests
{
    private const int Pgid = 4242;
    private const long Ticks = 99;

    [Test]
    [Arguments(0, SandboxGroupKillOutcome.Deferred)]
    [Arguments(SandboxSignalCalls.Esrch, SandboxGroupKillOutcome.Gone)]
    public async Task Foreign_WhenTheLeaderIsGoneAtTheOpen_ProbesTheGroupWithoutSignalling(int probeErrno, SandboxGroupKillOutcome expected)
    {
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Gone,
            KillResult = (_, _) => probeErrno
        };

        var outcome = await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks);

        AssertEx.Equal(expected, outcome, "an absent leader is not an empty group");
        AssertEx.Equal("open 4242,kill -4242 0", calls.Log());
    }

    [Test]
    public async Task Foreign_WhenTheOpenFails_DefersAndSendsNothing()
    {
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Failed
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242", calls.Log(), "a foreign pid never takes a numeric fallback on an operational failure");
    }

    [Test]
    public async Task Foreign_WhenTheStatIsUnreadableAfterTheOpen_DefersRatherThanCallingItGone()
    {
        var calls = new SandboxSignalCalls
        {
            Stat = static _ => LinuxProcStat.Unreadable
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242,send 0", calls.Log());
    }

    [Test]
    public async Task Foreign_WhenTheStatIsMissingAndTheLeaderIsGone_ProbesTheGroup()
    {
        var calls = new SandboxSignalCalls
        {
            Stat = static _ => LinuxProcStat.Missing,
            SendResult = static (_, _) => LinuxPidFdSendStatus.Gone
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Gone, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242,send 0,kill -4242 0", calls.Log());
    }

    [Test]
    public async Task Foreign_WhenTheTicksDiffer_IsAMismatchAndSendsNothing()
    {
        var calls = new SandboxSignalCalls
        {
            Stat = static _ => SandboxSignalCalls.Parsed(startTicks: Ticks + 1)
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Mismatch, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242", calls.Log());
    }

    [Test]
    public async Task Foreign_WhenTheLeaderDoesNotLeadItsGroup_KillsOnlyTheLeaderThroughThePin()
    {
        var calls = new SandboxSignalCalls
        {
            Stat = static _ => SandboxSignalCalls.Parsed(startTicks: Ticks, processGroupId: 1000)
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Killed, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242,send 9", calls.Log());
    }

    [Test]
    public async Task Foreign_AZombieLeaderWithTheRecordedTicks_StillReachesItsGroupThroughThePin()
    {
        var calls = new SandboxSignalCalls
        {
            Stat = static _ => SandboxSignalCalls.Parsed(startTicks: Ticks, exited: true),
            SendResult = static (signal, group) => signal == 0 && group ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Killed, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242,send 15 group,send 0 group", calls.Log(), "the group emptied within the grace: no KILL");
    }

    [Test]
    public async Task Foreign_WhenAMemberOutlivesTheGrace_KillsTheGroupThroughThePin()
    {
        var time = new ManualTimeProvider();
        var calls = new SandboxSignalCalls();

        var outcome = await DriveAsync(calls.Killer(time).KillProcessGroupAsync(Pgid, Ticks), time);

        AssertEx.Equal(SandboxGroupKillOutcome.Killed, outcome);
        AssertEx.Equal("send 9 group", calls.Calls[^1]);
        AssertEx.Empty(calls.NumericSignals);
    }

    [Test]
    public async Task Foreign_WhenTheGroupSendFails_DefersAndSendsNothingNumeric()
    {
        var calls = new SandboxSignalCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.Failed : LinuxPidFdSendStatus.Sent
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242,send 15 group", calls.Log());
    }

    [Test]
    public async Task Foreign_WithoutTheGroupFlag_WhileTheLeaderLives_SendsANumericTermThenKill()
    {
        var time = new ManualTimeProvider();
        var calls = new SandboxSignalCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent,
            KillResult = static (_, _) => 0
        };

        var outcome = await DriveAsync(calls.Killer(time).KillProcessGroupAsync(Pgid, Ticks), time);

        AssertEx.Equal(SandboxGroupKillOutcome.Killed, outcome);
        AssertEx.Equal("kill -4242 15,kill -4242 9", string.Join(',', calls.NumericSignals));
    }

    [Test]
    public async Task Foreign_WithoutTheGroupFlag_WhenTheLeaderDiesAfterTheTerm_SendsNoKill_AndDefersWhileMembersRemain()
    {
        var time = new ManualTimeProvider();
        var calls = new SandboxSignalCalls
        {
            KillResult = static (_, _) => 0
        };
        calls.SendResult = (_, group) =>
        {
            if (group)
            {
                return LinuxPidFdSendStatus.GroupFlagUnsupported;
            }

            return calls.Calls.Contains("kill -4242 15") ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent;
        };

        var outcome = await DriveAsync(calls.Killer(time).KillProcessGroupAsync(Pgid, Ticks), time);

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, outcome);
        AssertEx.Equal("kill -4242 15", string.Join(',', calls.NumericSignals), "no signal may follow the leader's death");
        AssertEx.Equal("kill -4242 0", calls.Calls[^1]);
    }

    [Test]
    public async Task Foreign_WithoutPidFd_VerifiesTheTicksBeforeEachNumericSignal()
    {
        var time = new ManualTimeProvider();
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Unsupported,
            KillResult = static (_, _) => 0
        };

        var outcome = await DriveAsync(calls.Killer(time).KillProcessGroupAsync(Pgid, Ticks), time);

        AssertEx.Equal(SandboxGroupKillOutcome.Killed, outcome);
        AssertEx.Equal("kill -4242 15,kill -4242 9", string.Join(',', calls.NumericSignals));
    }

    [Test]
    public async Task Foreign_WithoutPidFd_WhenTheLeaderDiesAfterTheTerm_SendsNoKill()
    {
        var time = new ManualTimeProvider();
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Unsupported,
            KillResult = static (_, _) => 0
        };
        calls.Stat = _ => calls.Calls.Contains("kill -4242 15") ? LinuxProcStat.Missing : SandboxSignalCalls.Parsed(Ticks);

        var outcome = await DriveAsync(calls.Killer(time).KillProcessGroupAsync(Pgid, Ticks), time);

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, outcome, "members answered the probe");
        AssertEx.Equal("kill -4242 15", string.Join(',', calls.NumericSignals));
    }

    [Test]
    public async Task Foreign_WithoutPidFd_WhenTheTicksDiffer_SendsNothing()
    {
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Unsupported,
            Stat = static _ => SandboxSignalCalls.Parsed(Ticks + 1)
        };

        AssertEx.Equal(SandboxGroupKillOutcome.Mismatch, await calls.Killer(TimeProvider.System).KillProcessGroupAsync(Pgid, Ticks));
        AssertEx.Equal("open 4242", calls.Log());
    }

    [Test]
    [Arguments(LinuxPidFdSendStatus.Sent, SandboxGroupKillOutcome.Killed)]
    [Arguments(LinuxPidFdSendStatus.Gone, SandboxGroupKillOutcome.Gone)]
    [Arguments(LinuxPidFdSendStatus.Failed, SandboxGroupKillOutcome.Deferred)]
    public async Task Owned_WithThePin_WalksTheTreeBeforeTheGroupKill(LinuxPidFdSendStatus kill, SandboxGroupKillOutcome expected)
    {
        var calls = new SandboxSignalCalls
        {
            SendResult = (signal, _) => signal == 9 ? kill : LinuxPidFdSendStatus.Sent
        };
        using var pin = calls.Open(Pgid);
        calls.Calls.Clear();

        var outcome = calls.Killer(TimeProvider.System).KillOwnedProcessGroup(pin, LinuxPidFdOpenStatus.Opened, Pgid, static () => false, calls.Tree);

        AssertEx.Equal(expected, outcome);
        AssertEx.Equal("send 0 group,tree,send 9 group", calls.Log(), "the tree walk runs while the leader still parents its descendants");
    }

    [Test]
    [Arguments(0, SandboxGroupKillOutcome.Killed)]
    [Arguments(SandboxSignalCalls.Esrch, SandboxGroupKillOutcome.Gone)]
    [Arguments(1, SandboxGroupKillOutcome.Deferred)]
    public async Task Owned_WithoutTheGroupFlag_WalksTheTreeFirst_ThenKillsTheGroupByNumberWhileTheLeaderIsUnreaped(int killErrno, SandboxGroupKillOutcome expected)
    {
        var calls = new SandboxSignalCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent,
            KillResult = (_, _) => killErrno
        };
        using var pin = calls.Open(Pgid);
        calls.Calls.Clear();

        var outcome = calls.Killer(TimeProvider.System).KillOwnedProcessGroup(pin, LinuxPidFdOpenStatus.Opened, Pgid, static () => false, calls.Tree);

        AssertEx.Equal(expected, outcome);
        // A numeric group KILL first would take the leader and orphan a descendant outside the group before the walk could find it.
        AssertEx.Equal("send 0 group,tree,send 0,kill -4242 9", calls.Log());
    }

    [Test]
    [Arguments(LinuxPidFdSendStatus.Gone)]
    [Arguments(LinuxPidFdSendStatus.Failed)]
    public async Task Owned_WithoutTheGroupFlag_WhenTheLeaderIsNotProvenUnreaped_SendsNothingDestructive(LinuxPidFdSendStatus leader)
    {
        var calls = new SandboxSignalCalls
        {
            SendResult = (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : leader,
            KillResult = static (_, _) => 0
        };
        using var pin = calls.Open(Pgid);
        calls.Calls.Clear();

        var outcome = calls.Killer(TimeProvider.System).KillOwnedProcessGroup(pin, LinuxPidFdOpenStatus.Opened, Pgid, static () => false, calls.Tree);

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, outcome, "members answered the probe");
        AssertEx.Equal("send 0 group,tree,send 0,kill -4242 0", calls.Log());
        AssertEx.Empty(calls.NumericSignals);
    }

    [Test]
    [Arguments(LinuxPidFdOpenStatus.Failed)]
    [Arguments(LinuxPidFdOpenStatus.Unsupported)]
    public async Task Owned_WithoutAPin_WalksTheTreeFirst_ThenKillsTheGroupByNumberWhileTheChildIsUnexited(LinuxPidFdOpenStatus status)
    {
        var calls = new SandboxSignalCalls
        {
            KillResult = static (_, _) => 0
        };

        var outcome = calls.Killer(TimeProvider.System).KillOwnedProcessGroup(null, status, Pgid, static () => false, calls.Tree);

        AssertEx.Equal(SandboxGroupKillOutcome.Killed, outcome);
        AssertEx.Equal("tree,kill -4242 9", calls.Log());
    }

    [Test]
    [Arguments(LinuxPidFdOpenStatus.Failed, 0, SandboxGroupKillOutcome.Deferred)]
    [Arguments(LinuxPidFdOpenStatus.Gone, 0, SandboxGroupKillOutcome.Deferred)]
    [Arguments(LinuxPidFdOpenStatus.Gone, SandboxSignalCalls.Esrch, SandboxGroupKillOutcome.Gone)]
    public async Task Owned_WhenTheLeaderExitedUnpinned_OnlyProbesTheGroup(LinuxPidFdOpenStatus status, int probeErrno, SandboxGroupKillOutcome expected)
    {
        var calls = new SandboxSignalCalls
        {
            KillResult = (_, _) => probeErrno
        };

        var outcome = calls.Killer(TimeProvider.System).KillOwnedProcessGroup(null, status, Pgid, static () => true, calls.Tree);

        AssertEx.Equal(expected, outcome);
        AssertEx.Equal("tree,kill -4242 0", calls.Log());
    }

    /// <summary>A descendant that left the group is out of the group kill's reach, so only a finished tree walk lets the marker go.</summary>
    [Test]
    [Arguments(LinuxPidFdOpenStatus.Opened, LinuxPidFdSendStatus.Sent)]
    [Arguments(LinuxPidFdOpenStatus.Opened, LinuxPidFdSendStatus.GroupFlagUnsupported)]
    [Arguments(LinuxPidFdOpenStatus.Gone, LinuxPidFdSendStatus.Sent)]
    public async Task Owned_WhenTheTreeKillIsIncomplete_DefersWhateverTheGroupOutcome(LinuxPidFdOpenStatus pinStatus, LinuxPidFdSendStatus groupSend)
    {
        var calls = new SandboxSignalCalls
        {
            TreeComplete = false,
            SendResult = (_, group) => group ? groupSend : LinuxPidFdSendStatus.Sent,
            KillResult = static (_, signal) => signal == 0 ? SandboxSignalCalls.Esrch : 0
        };
        using var pin = pinStatus == LinuxPidFdOpenStatus.Opened ? calls.Open(Pgid) : null;

        var outcome = calls.Killer(TimeProvider.System).KillOwnedProcessGroup(pin, pinStatus, Pgid, static () => true, calls.Tree);

        AssertEx.Equal(SandboxGroupKillOutcome.Deferred, outcome, "the group kill alone would have reported Killed or Gone");
        AssertEx.Contains(calls.Calls, "tree");
    }

    [Test]
    public async Task GetProcessStartTicks_IsNullForAnUnreadableAndAMissingStat_AndTheTicksOtherwise()
    {
        var calls = new SandboxSignalCalls();
        calls.Stat = pid => pid switch
        {
            1 => LinuxProcStat.Unreadable,
            2 => LinuxProcStat.Missing,
            _ => SandboxSignalCalls.Parsed(Ticks)
        };
        var killer = calls.Killer(TimeProvider.System);

        AssertEx.Null(killer.GetProcessStartTicks(1));
        AssertEx.Null(killer.GetProcessStartTicks(2));
        AssertEx.Equal(Ticks, killer.GetProcessStartTicks(3));
        await Task.CompletedTask;
    }
}
