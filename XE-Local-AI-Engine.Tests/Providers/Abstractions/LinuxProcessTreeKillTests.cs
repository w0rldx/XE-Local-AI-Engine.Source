namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using System.ComponentModel;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The pidfd tree walk over a scripted process table: who is pinned, verified, stopped and killed, and what the walk reports.</summary>
/// <remarks>Nothing reaches the kernel. Every walk ends with <see cref="FakeProcessTable.AssertCleanup" /> unless a refused SIGKILL is the point.</remarks>
[TUnit.Core.Category(TestCategories.Unit)]
public sealed class LinuxProcessTreeKillTests
{
    private const int Root = 10;

    [Test]
    public void RootOpenGone_IsCompleteAndSendsNothing()
    {
        var table = new FakeProcessTable();

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Equal("open 10", string.Join(',', table.Log));
    }

    [Test]
    [Arguments(LinuxPidFdOpenStatus.Failed)]
    [Arguments(LinuxPidFdOpenStatus.Unsupported)]
    public void RootOpenFailedOrUnsupported_UsesTheRuntimeFallback(LinuxPidFdOpenStatus status)
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1).OpenStatus = status;

        AssertEx.Equal(LinuxProcessTreeKillResult.FallbackKilled, table.Kill(Root));
        AssertEx.Equal("open 10,fallback", string.Join(',', table.Log));
        table.AssertCleanup();
    }

    [Test]
    public void HostNotVerifiable_UsesTheFallbackWithoutOpening()
    {
        var table = new FakeProcessTable
        {
            HostVerifiable = false
        };
        table.Add(Root, 1);

        AssertEx.Equal(LinuxProcessTreeKillResult.FallbackKilled, table.Kill(Root));
        AssertEx.Equal("fallback", string.Join(',', table.Log));
    }

    [Test]
    public void RootExitedAfterTheOpen_SendsNothingAndClosesThePin()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root, rootExited: () => true));
        AssertEx.Equal("open 10", string.Join(',', table.Log));
        table.AssertCleanup();
    }

    [Test]
    public void RootStatUnreadable_IsKilledThroughItsPinWithoutTheFallback()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1).StatOutcome = LinuxProcStatOutcome.Unreadable;

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Equal("open 10,kill 10", string.Join(',', table.Log));
        table.AssertCleanup();
    }

    [Test]
    public void RootZombie_IsCompleteWithoutAStop()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1, threads: ["Z"]);

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Empty(table[Root].Signals);
        table.AssertCleanup();
    }

    [Test]
    public void RootLeaderZombieWorkerThreadsLive_StoppedAndKilled()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1, threads: ["Z", "R"]);

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Equal("19,9", string.Join(',', table[Root].Signals));
        table.AssertCleanup();
    }

    [Test]
    public void Tree_StopsTopDownAndKillsLeavesFirst_IncludingAChildInAnotherGroup()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        table.Add(12, 11);
        table.Add(13, Root).ProcessGroupId = 13;

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Equal("stop 10,stop 11,stop 12,stop 13", string.Join(',', table.Log.Where(static call => call.StartsWith("stop", StringComparison.Ordinal))));
        AssertEx.Equal("kill 13,kill 12,kill 11,kill 10", string.Join(',', table.Log.Where(static call => call.StartsWith("kill", StringComparison.Ordinal))));
        table.AssertCleanup();
    }

    [Test]
    public void ChildPidReusedBetweenScanAndOpen_IsNeverSignalled()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        var foreign = new FakeTreeProcess
        {
            Pid = 11,
            ParentPid = 1,
            StartTicks = 5000
        };
        table.Add(11, Root).ReplacedBeforeOpen = foreign;

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Empty(foreign.Signals);
        table.AssertCleanup();
    }

    [Test]
    public void ReparentedEscapee_IsIncompleteAndNotSignalled()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root).ReparentedOnOpen = true;

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Empty(table[11].Signals);
        table.AssertCleanup();
    }

    [Test]
    [Arguments(1000L, LinuxProcessTreeKillResult.Complete, true)]
    [Arguments(999L, LinuxProcessTreeKillResult.Incomplete, false)]
    public void ChildStartTicks_EqualToTheParentsIsAccepted_EarlierIsRejected(long childTicks, LinuxProcessTreeKillResult expected, bool killed)
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1, startTicks: 1000);
        table.Add(11, Root, startTicks: childTicks);

        AssertEx.Equal(expected, table.Kill(Root));
        AssertEx.Equal(killed, table[11].Signals.Contains(9));
        table.AssertCleanup();
    }

    [Test]
    public void ParentGoneBeforeTheChildIsVerified_RejectsTheChild()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        table.BeforeCall = call =>
        {
            if (call == "open 11")
            {
                table[Root].SendOverride = static signal => signal == 0 ? LinuxPidFdSendStatus.Gone : null;
            }
        };

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Empty(table[11].Signals);
        table.AssertCleanup();
    }

    [Test]
    [Arguments(LinuxPidFdOpenStatus.Gone, LinuxProcessTreeKillResult.Complete)]
    [Arguments(LinuxPidFdOpenStatus.Failed, LinuxProcessTreeKillResult.Incomplete)]
    [Arguments(LinuxPidFdOpenStatus.Unsupported, LinuxProcessTreeKillResult.Incomplete)]
    public void ChildOpenStatus_NeverFallsBackToANumber(LinuxPidFdOpenStatus status, LinuxProcessTreeKillResult expected)
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root).OpenStatus = status;

        AssertEx.Equal(expected, table.Kill(Root));
        AssertEx.Empty(table[11].Signals);
        AssertEx.False(table.Log.Contains("fallback"), "a child open failure must not take the numeric fallback");
        table.AssertCleanup();
    }

    [Test]
    [Arguments(LinuxProcStatOutcome.Missing, LinuxProcessTreeKillResult.Complete)]
    [Arguments(LinuxProcStatOutcome.Unreadable, LinuxProcessTreeKillResult.Incomplete)]
    public void ChildStatAfterTheOpen_MissingIsNeutral_UnreadableIsIncomplete(LinuxProcStatOutcome outcome, LinuxProcessTreeKillResult expected)
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        table.BeforeCall = call =>
        {
            if (call == "open 11")
            {
                table[11].StatOutcome = outcome;
            }
        };

        AssertEx.Equal(expected, table.Kill(Root));
        AssertEx.Empty(table[11].Signals);
        table.AssertCleanup();
    }

    [Test]
    public void ChildZombie_IsNotStopped()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root, threads: ["Z"]);

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Empty(table[11].Signals);
        table.AssertCleanup();
    }

    [Test]
    public void LateForkAfterAStop_IsFoundByTheNextRound()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        table.BeforeCall = call =>
        {
            if (call == "stop 11")
            {
                _ = table.Add(14, 11);
            }
        };

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Equal("19,9", string.Join(',', table[14].Signals));
        AssertEx.Equal(3, table.Log.Count(static call => call == "list"));
        table.AssertCleanup();
    }

    [Test]
    [Arguments("R")]
    [Arguments("t")]
    public void AThreadNotStopped_IsNotConfirmed(string workerState)
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1, threads: ["S", workerState], unstoppableThread: 1);

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Equal("19,9", string.Join(',', table[Root].Signals));
        table.AssertCleanup();
    }

    [Test]
    public void StopConfirmTimeout_DiscoveryEndsWithoutTheTotalBound()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1, threads: ["D"], unstoppableThread: 0);
        table.Add(11, Root);

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Equal("19,9", string.Join(',', table[11].Signals));
        AssertEx.Equal(2, table.Log.Count(static call => call == "list"));
        table.AssertCleanup();
    }

    [Test]
    public void StopSendFailed_IsIncompleteAndStillKilled()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1).SendOverride = static signal => signal == 19 ? LinuxPidFdSendStatus.Failed : null;

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Contains(table.Log, "kill 10");
        table.AssertCleanup();
    }

    /// <summary>A filter allowing <c>pidfd_open</c> but refusing <c>pidfd_send_signal</c> (EPERM, ENOSYS): no pin can signal, so the runtime fallback runs.</summary>
    [Test]
    [Arguments(1)]
    [Arguments(38)]
    public void RootSendRefused_SendsNothingMoreAndUsesTheRuntimeFallback(int errno)
    {
        var table = new FakeProcessTable();
        var root = table.Add(Root, 1);
        root.SendOverride = static _ => LinuxPidFdSendStatus.Failed;
        root.SendErrno = errno;
        table.Add(11, Root);

        AssertEx.Equal(LinuxProcessTreeKillResult.FallbackKilled, table.Kill(Root));
        AssertEx.Equal("open 10,probe 10,fallback", string.Join(',', table.Log));
        table.AssertCleanup();
    }

    [Test]
    public void KillSendFailed_ReportsKillFailedAndStillKillsTheRest()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root).SendOverride = static signal => signal == 9 ? LinuxPidFdSendStatus.Failed : null;

        AssertEx.Equal(LinuxProcessTreeKillResult.KillFailed, table.Kill(Root));
        AssertEx.Equal("19,9", string.Join(',', table[Root].Signals));
        AssertEx.True(table.Opened.TrueForAll(static pidFd => pidFd.IsClosed), "an opened pidfd was left open");
    }

    [Test]
    public void ThrowBetweenOpenAndInsertion_ClosesThatFdAndKillsTheRoot()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        var opened = false;
        table.BeforeCall = call =>
        {
            opened |= call == "open 11";
            if (opened && call == "stat 11")
            {
                throw new IOException("procfs went away");
            }
        };

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Empty(table[11].Signals);
        AssertEx.Equal("19,9", string.Join(',', table[Root].Signals));
        table.AssertCleanup();
    }

    [Test]
    public void ThrowAfterStoppingTheRoot_StillKillsIt()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.BeforeCall = static call =>
        {
            if (call == "list")
            {
                throw new IOException("/proc cannot be listed");
            }
        };

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Equal("19,9", string.Join(',', table[Root].Signals));
        table.AssertCleanup();
    }

    [Test]
    public void ThrowOnTheFirstReverseKill_StillKillsEveryOtherMember()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        table.Add(12, 11);
        table.BeforeCall = static call =>
        {
            if (call == "kill 12")
            {
                throw new InvalidOperationException("the send failed oddly");
            }
        };

        AssertEx.Equal(LinuxProcessTreeKillResult.KillFailed, table.Kill(Root));
        AssertEx.Equal("kill 11,kill 10", string.Join(',', table.Log.Where(static call => call.StartsWith("kill", StringComparison.Ordinal))));
        AssertEx.True(table.Opened.TrueForAll(static pidFd => pidFd.IsClosed), "an opened pidfd was left open");
    }

    [Test]
    public void OutOfMemoryAfterStoppingRootAndChild_PropagatesAfterKillingAndClosingEveryMember()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        var lists = 0;
        table.BeforeCall = call =>
        {
            // The second round's scan starts only after the root and the child were both stop-confirmed. InsufficientMemoryException is an
            // OutOfMemoryException that is not runtime-reserved (CA2201), so the walk's "not OutOfMemoryException" filters see it as one.
            if (call == "list" && ++lists == 2)
            {
                throw new InsufficientMemoryException("injected allocation failure");
            }
        };

        _ = AssertEx.Throws<InsufficientMemoryException>(() => table.Kill(Root));

        AssertEx.Equal("kill 11,kill 10", string.Join(',', table.Log.Where(static call => call.StartsWith("kill", StringComparison.Ordinal))));
        AssertEx.Equal("19,9", string.Join(',', table[11].Signals));
        AssertEx.Equal("19,9", string.Join(',', table[Root].Signals));
        table.AssertCleanup();
    }

    [Test]
    public void DeadlineExpiresInsideTheFirstScan_StopsDiscoveryAndKillsTheMembers()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(11, Root);
        table.Add(12, Root);
        table.BeforeCall = call =>
        {
            if (call == "stat 11")
            {
                table.Time.Advance(TimeSpan.FromSeconds(1));
            }
        };

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Empty(table[12].Signals);
        AssertEx.Equal("19,9", string.Join(',', table[Root].Signals));
        table.AssertCleanup();
    }

    [Test]
    public void MaxMembersReached_IsIncomplete()
    {
        var table = new FakeProcessTable
        {
            MaxMembers = 2
        };
        table.Add(Root, 1);
        table.Add(11, Root);
        table.Add(12, Root);

        AssertEx.Equal(LinuxProcessTreeKillResult.Incomplete, table.Kill(Root));
        AssertEx.Empty(table[12].Signals);
        table.AssertCleanup();
    }

    [Test]
    public void UnreadableScanEntry_EarnsOneMoreRoundThenIsNotAttributed()
    {
        var table = new FakeProcessTable();
        table.Add(Root, 1);
        table.Add(20, 1).StatOutcome = LinuxProcStatOutcome.Unreadable;

        AssertEx.Equal(LinuxProcessTreeKillResult.Complete, table.Kill(Root));
        AssertEx.Equal(2, table.Log.Count(static call => call == "list"));
        table.AssertCleanup();
    }

    [Test]
    [Arguments("exited", LinuxProcessTreeKillResult.Complete)]
    [Arguments("killed", LinuxProcessTreeKillResult.FallbackKilled)]
    [Arguments("exited-during-kill", LinuxProcessTreeKillResult.Complete)]
    [Arguments("refused", LinuxProcessTreeKillResult.Incomplete)]
    [Arguments("descendant-survived", LinuxProcessTreeKillResult.Incomplete)]
    [Arguments("unsupported", LinuxProcessTreeKillResult.Incomplete)]
    public void RuntimeTreeKill_MapsEveryOutcome(string outcome, LinuxProcessTreeKillResult expected)
    {
        var result = LinuxProcessTreeKill.RuntimeTreeKill(() => outcome == "exited", () =>
        {
            Exception? failure = outcome switch
            {
                "exited-during-kill" => new InvalidOperationException("exited"),
                "refused" => new Win32Exception(1),
                "descendant-survived" => new AggregateException(new Win32Exception(1)),
                "unsupported" => new NotSupportedException(),
                _ => null
            };
            if (failure is not null)
            {
                throw failure;
            }
        });

        AssertEx.Equal(expected, result);
    }
}
