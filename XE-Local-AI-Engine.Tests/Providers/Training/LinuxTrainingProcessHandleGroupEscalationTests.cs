namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static LinuxTrainingProcessGroupTests;
using static LinuxTrainingProcessHandlePidFdTests;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     <see cref="ITrainingProcessHandle.KillGroup" /> escalating through a pinned fd to group members that outlive their leader: scripted sends over
///     a real child, the group poll over a manual clock, and one real-kernel group.
/// </summary>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class LinuxTrainingProcessHandleGroupEscalationTests
{
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    [Test]
    public void ExitedLeader_WhenTheGroupOutlivesTheGrace_TermsThenKillsThroughTheFd()
    {
        var calls = new FakePidFdCalls();
        var waits = new List<TimeSpan>();
        using var child = ExitedChild();
        using var handle = child.Handle(calls, Pinned(calls, child), timeProvider: new ManualTimeProvider(), waitForGroupEmpty: Record(waits, GroupWaitResult.PresentAtDeadline));

        handle.KillGroup();

        AssertEx.Equal("send 0 group,send 15 group,send 9 group", string.Join(',', calls.Calls), "No call leaves the fd once the leader has exited.");
        AssertEx.Equal(KillGrace, waits.Single(), "No time passed since the SIGTERM, so the whole grace remains.");
    }

    [Test]
    public void ExitedLeader_WhenTheProbeReadsGone_SendsNothingElse()
    {
        var calls = new FakePidFdCalls
        {
            SendResult = static (signal, _) => signal == 0 ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent
        };
        var waits = new List<TimeSpan>();
        using var child = ExitedChild();
        using var handle = child.Handle(calls, Pinned(calls, child), waitForGroupEmpty: Record(waits, GroupWaitResult.PresentAtDeadline));

        handle.KillGroup();

        AssertEx.Equal("send 0 group", string.Join(',', calls.Calls), "An empty group gets no signal.");
        AssertEx.Empty(waits);
    }

    /// <summary>An unsupported group flag never falls back to the pid-only probe and numeric kill of the leader-alive path: the pgid may be reused.</summary>
    [Test]
    [Arguments(LinuxPidFdSendStatus.GroupFlagUnsupported)]
    [Arguments(LinuxPidFdSendStatus.Failed)]
    public void ExitedLeader_WhenTheProbeIsRefused_SendsNothingElse(LinuxPidFdSendStatus probe)
    {
        var calls = new FakePidFdCalls
        {
            SendResult = (_, _) => probe
        };
        var waits = new List<TimeSpan>();
        using var child = ExitedChild();
        using var handle = child.Handle(calls, Pinned(calls, child), waitForGroupEmpty: Record(waits, GroupWaitResult.PresentAtDeadline));

        handle.KillGroup();

        AssertEx.Equal("send 0 group", string.Join(',', calls.Calls), "Neither a pid-only probe nor a numeric kill follows.");
        AssertEx.Empty(waits);
    }

    [Test]
    [Arguments(nameof(GroupWaitResult.Empty))]
    [Arguments(nameof(GroupWaitResult.ProbeUnavailable))]
    public void ExitedLeader_WhenTheGroupIsNotProvenPresentAtTheDeadline_SendsNoSigkill(string waitName)
    {
        var wait = Enum.Parse<GroupWaitResult>(waitName);
        var calls = new FakePidFdCalls();
        using var child = ExitedChild();
        using var handle = child.Handle(calls, Pinned(calls, child), waitForGroupEmpty: _ => wait);

        handle.KillGroup();

        AssertEx.Equal("send 0 group,send 15 group", string.Join(',', calls.Calls), "Only a member seen at the deadline is killed.");
    }

    [Test]
    public void LiveLeader_ExitingDuringTheGrace_LeavesTheRestOfTheGraceToTheGroup_WithOneSigterm()
    {
        var clock = new ManualTimeProvider();
        var calls = new FakePidFdCalls();
        var waits = new List<TimeSpan>();
        using var child = new TokenChild();
        using var handle = child.Handle(calls,
            Pinned(calls, child),
            waitForExit: _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(1.5));
                return true;
            },
            timeProvider: clock,
            waitForGroupEmpty: Record(waits, GroupWaitResult.PresentAtDeadline));

        handle.KillGroup();

        AssertEx.Equal("send 15 group,send 9 group", string.Join(',', calls.Calls), "The SIGTERM is never re-sent.");
        AssertEx.Equal(TimeSpan.FromSeconds(0.5), waits.Single(), "One grace runs from the first SIGTERM.");
        child.End();
    }

    /// <summary>A pinned group's leader waits only for what the SIGTERM send left of the grace; a pid-only target keeps today's full wait.</summary>
    [Test]
    [Arguments(true, 0.5)]
    [Arguments(false, 2.0)]
    public void LiveLeader_TheTimeTheSigtermTakes_ComesOutOfTheLeadersWait(bool leadsItsGroup, double expectedSeconds)
    {
        var clock = new ManualTimeProvider();
        var calls = new FakePidFdCalls
        {
            SendResult = (signal, _) =>
            {
                if (signal == 15)
                {
                    clock.Advance(TimeSpan.FromSeconds(1.5));
                }

                return LinuxPidFdSendStatus.Sent;
            }
        };
        var leaderWaits = new List<TimeSpan>();
        using var child = new TokenChild();
        using var handle = child.Handle(calls,
            Pinned(calls, child),
            waitForExit: timeout =>
            {
                leaderWaits.Add(timeout);
                return false;
            },
            timeProvider: clock,
            waitForGroupEmpty: static _ => GroupWaitResult.Empty,
            pgid: leadsItsGroup ? null : child.Process.Id + 1);

        handle.KillGroup();

        AssertEx.Equal(TimeSpan.FromSeconds(expectedSeconds), leaderWaits.Single());
        child.End();
    }

    [Test]
    public void ExitedLeader_TheTimeTheSigtermTakes_ComesOutOfTheGrace()
    {
        var clock = new ManualTimeProvider();
        var calls = new FakePidFdCalls
        {
            SendResult = (signal, _) =>
            {
                if (signal == 15)
                {
                    clock.Advance(TimeSpan.FromSeconds(0.5));
                }

                return LinuxPidFdSendStatus.Sent;
            }
        };
        var waits = new List<TimeSpan>();
        using var child = ExitedChild();
        using var handle = child.Handle(calls, Pinned(calls, child), timeProvider: clock, waitForGroupEmpty: Record(waits, GroupWaitResult.Empty));

        handle.KillGroup();

        AssertEx.Equal(TimeSpan.FromSeconds(1.5), waits.Single());
    }

    /// <summary>The real poll, once the SIGTERM has used up the grace: one probe decides. Each probe moves the clock, so a poll the real sleep paces still ends.</summary>
    [Test]
    [Arguments(LinuxPidFdSendStatus.Sent, "send 0 group,send 15 group,send 0 group,send 9 group")]
    [Arguments(LinuxPidFdSendStatus.Gone, "send 0 group,send 15 group,send 0 group")]
    public void ExitedLeader_WithNoGraceLeft_KillsOnlyAGroupTheOneProbeFinds(LinuxPidFdSendStatus atDeadline, string expected)
    {
        var clock = new ManualTimeProvider();
        var probes = 0;
        var calls = new FakePidFdCalls
        {
            SendResult = (signal, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(signal == 15 ? 3 : 1));
                return signal == 0 && ++probes > 1 ? atDeadline : LinuxPidFdSendStatus.Sent;
            }
        };
        using var child = ExitedChild();
        using var handle = child.Handle(calls, Pinned(calls, child), timeProvider: clock);

        handle.KillGroup();

        AssertEx.Equal(expected, string.Join(',', calls.Calls));
    }

    [Test]
    public void ExitedLeader_WhenTheSigtermFails_SendsNoSigkill()
    {
        var calls = new FakePidFdCalls
        {
            SendResult = static (signal, _) => signal == 15 ? LinuxPidFdSendStatus.Failed : LinuxPidFdSendStatus.Sent
        };
        var waits = new List<TimeSpan>();
        using var child = ExitedChild();
        using var handle = child.Handle(calls, Pinned(calls, child), waitForGroupEmpty: Record(waits, GroupWaitResult.PresentAtDeadline));

        handle.KillGroup();

        AssertEx.Equal("send 0 group,send 15 group", string.Join(',', calls.Calls));
        AssertEx.Empty(waits);
    }

    /// <summary>Only a pinned fd still names the group once the leader is gone; every other state, and a pid-only target, stays silent.</summary>
    [Test]
    [Arguments(nameof(TrainerPidFdState.Legacy), true)]
    [Arguments(nameof(TrainerPidFdState.Unpinned), true)]
    [Arguments(nameof(TrainerPidFdState.Refused), true)]
    [Arguments(nameof(TrainerPidFdState.Pinned), false)]
    public void ExitedLeader_WithoutAPinnedGroup_SendsNothing(string stateName, bool leadsItsGroup)
    {
        var state = Enum.Parse<TrainerPidFdState>(stateName);
        var calls = new FakePidFdCalls();
        var waits = new List<TimeSpan>();
        using var child = ExitedChild();
        var pin = state == TrainerPidFdState.Pinned ? Pinned(calls, child) : new LinuxTrainingProcessSpawner.TrainerPin(state, PidFd: null);
        using var handle = child.Handle(calls,
            pin,
            waitForGroupEmpty: Record(waits, GroupWaitResult.PresentAtDeadline),
            pgid: leadsItsGroup ? null : child.Process.Id + 1);

        handle.KillGroup();

        AssertEx.Empty(calls.Calls);
        AssertEx.Empty(waits);
    }

    [Test]
    public void Poll_WhenTheProbeFails_IsUnavailable_AndStopsProbing()
    {
        var clock = new ManualTimeProvider();
        var results = new Queue<LinuxPidFdSendStatus>([LinuxPidFdSendStatus.Sent, LinuxPidFdSendStatus.Sent, LinuxPidFdSendStatus.Failed]);
        var calls = new FakePidFdCalls
        {
            SendResult = (_, _) => results.Dequeue()
        };

        var result = LinuxTrainingProcessHandle.PollGroupUntilEmpty(calls.Open(1), calls.Ops, clock, clock.Advance, KillGrace, NullLogger.Instance);

        AssertEx.Equal(GroupWaitResult.ProbeUnavailable, result);
        AssertEx.Equal("open 1,send 0 group,send 0 group,send 0 group", string.Join(',', calls.Calls));
    }

    [Test]
    public void Poll_WhenAMemberStaysToTheDeadline_IsPresent_AndCapsTheLastSleep()
    {
        var clock = new ManualTimeProvider();
        var sleeps = new List<TimeSpan>();
        var calls = new FakePidFdCalls();

        var result = LinuxTrainingProcessHandle.PollGroupUntilEmpty(calls.Open(1),
            calls.Ops,
            clock,
            interval =>
            {
                sleeps.Add(interval);
                clock.Advance(interval);
            },
            TimeSpan.FromMilliseconds(120),
            NullLogger.Instance);

        AssertEx.Equal(GroupWaitResult.PresentAtDeadline, result);
        AssertEx.Equal(4, calls.Calls.Count(static call => call == "send 0 group"), "Probes at 0, 50, 100 and 120 ms.");
        AssertEx.Equal("50,50,20", string.Join(',', sleeps.Select(static sleep => sleep.TotalMilliseconds.ToString(CultureInfo.InvariantCulture))));
    }

    [Test]
    public void Poll_WhenTheGroupReadsGone_IsEmpty_WithoutSleeping()
    {
        var clock = new ManualTimeProvider();
        var sleeps = new List<TimeSpan>();
        var calls = new FakePidFdCalls
        {
            SendResult = static (_, _) => LinuxPidFdSendStatus.Gone
        };

        var result = LinuxTrainingProcessHandle.PollGroupUntilEmpty(calls.Open(1), calls.Ops, clock, sleeps.Add, KillGrace, NullLogger.Instance);

        AssertEx.Equal(GroupWaitResult.Empty, result);
        AssertEx.Equal("open 1,send 0 group", string.Join(',', calls.Calls));
        AssertEx.Empty(sleeps);
    }

    /// <summary>
    ///     A member that ignores SIGTERM outlives its reaped leader; the production handle, pinned while the leader lived, kills it through the fd. A
    ///     zombie counts as gone: only pid 1 or a subreaper can reap the orphan.
    /// </summary>
    [Test]
    public async Task RealKernel_ExitedLeader_KillsAMemberThatIgnoresSigterm()
    {
        if (Environment.OSVersion.Version < new Version(6, 9))
        {
            Skip.Test($"BLOCKED: kernel {Environment.OSVersion.Version} predates PIDFD_SIGNAL_PROCESS_GROUP (Linux 6.9).");
        }

        if (!File.Exists(Setsid))
        {
            Skip.Test($"BLOCKED: {Setsid} is absent, so the leader cannot lead a group of its own.");
        }

        var marker = Path.Combine(Path.GetTempPath(), $"xe-kill-group-{Guid.NewGuid():N}");
        var startInfo = new ProcessStartInfo(Setsid)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in (string[])["/bin/sh", "-c", "(trap '' TERM; : > \"$1\"; exec /bin/sleep 30) </dev/null >/dev/null 2>&1 & echo $!; read line", "sh", marker])
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var leader = Process.Start(startInfo) ?? throw new InvalidOperationException("The leader did not start.");
        OwnedProcess? member = null;
        try
        {
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            var line = await leader.StandardOutput.ReadLineAsync(budget.Token) ?? throw new InvalidOperationException("The script printed no member pid.");
            var memberPid = int.Parse(line, CultureInfo.InvariantCulture);
            member = new OwnedProcess
            {
                ProcessId = memberPid,
                StartTicks = LinuxTrainingProcessInspector.ReadStatOrNull(memberPid)?.StartTicks ?? throw new InvalidOperationException("The member exited at once.")
            };
            // The handle owns the fd and closes it on dispose; a second close of a SafeHandle is a no-op.
            using var pidFd = LinuxPidFd.Open(leader.Id);
            if (pidFd.OpenStatus != LinuxPidFdOpenStatus.Opened)
            {
                Skip.Test($"BLOCKED: pidfd_open on the leader read {pidFd.OpenStatus} (errno {pidFd.OpenErrno}).");
            }

            using var handle = new LinuxTrainingProcessHandle(leader,
                new TrainingLaunchReceipt
                {
                    Pid = leader.Id,
                    Pgid = leader.Id,
                    ExecutablePath = null,
                    StartTicks = LinuxTrainingProcessInspector.ReadStatOrNull(leader.Id)?.StartTicks ?? throw new InvalidOperationException("The leader exited at once."),
                    RunToken = RunToken
                },
                Channel.CreateUnbounded<string>(),
                NullLogger.Instance,
                TimeProvider.System,
                new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Pinned, pidFd));
            await AssertEx.EventuallyAsync(() => File.Exists(marker), TestBudgets.Contended, "The member never reported that it ignores SIGTERM.");
            await leader.StandardInput.WriteLineAsync("exit");
            await leader.WaitForExitAsync(budget.Token);
            AssertEx.Equal(TrainingProcessPresence.Alive, LinuxTrainingProcessInspector.ReadStat(memberPid).Presence, "The premise: the member outlived its leader.");

            handle.KillGroup();

            var owned = member;
            await AssertEx.EventuallyAsync(() => IsGone(owned), TimeSpan.FromSeconds(5), "The member outlived the SIGTERM grace, so only the SIGKILL ends it.");
        }
        finally
        {
            member?.KillIfStillOurs();
            File.Delete(marker);
        }
    }

    /// <summary>Missing, recycled, or a zombie: no longer the running member.</summary>
    private static bool IsGone(OwnedProcess member)
    {
        var read = LinuxTrainingProcessInspector.ReadStat(member.ProcessId);
        return !LinuxTrainingProcessInspector.MaySignal(read, member.StartTicks) || read.Presence == TrainingProcessPresence.Gone;
    }

    private static TokenChild ExitedChild()
    {
        var child = new TokenChild();
        child.End();
        return child;
    }

    /// <summary>An fd from the scripted calls, with its open cleared from the log, as the spawn would have left it.</summary>
    private static LinuxTrainingProcessSpawner.TrainerPin Pinned(FakePidFdCalls calls, TokenChild child)
    {
        var pidFd = calls.Open(child.Process.Id);
        calls.Calls.Clear();
        return new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Pinned, pidFd);
    }

    private static Func<TimeSpan, GroupWaitResult> Record(List<TimeSpan> waits, GroupWaitResult result) =>
        timeout =>
        {
            waits.Add(timeout);
            return result;
        };
}
