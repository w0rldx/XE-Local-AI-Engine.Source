namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The <see cref="LinuxProcessGroupHandle" /> kill matrix over scripted pidfd and kill calls, around a real child that stands in for
///     the leader: the pin taken at the wrap, the window before <c>setsid</c>, and the old-kernel leader-death rule.
/// </summary>
/// <remarks>Nothing scripted reaches the kernel; the real child is killed by each test itself.</remarks>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class LinuxProcessGroupHandleSignalTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(100);

    [Test]
    public async Task Wrap_WhenTheChildExitedBeforeThePinWasChecked_DropsThePin_AndOnlyProbes()
    {
        using var child = Start("true");
        await child.WaitForExitAsync();
        var calls = new SandboxSignalCalls();
        using var handle = Wrap(child, calls);

        AssertEx.True(handle.Terminate(), "the probe found the group empty");
        AssertEx.Equal($"open {child.Id},kill -{child.Id} 0", calls.Log(), "a pin that may name a recycled pid is never sent through");
    }

    [Test]
    public async Task TreeKill_WhenTheOpenFindsTheLeaderGone_OnlyProbes_AndReportsMembersLeft()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Gone,
            KillResult = static (_, _) => 0
        };
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.False(handle.Terminate(), "members answered the probe");
            AssertEx.Empty(calls.NumericSignals);
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task TreeKill_WhenTheLeaderExitsInTheGrace_KillsTheSurvivingGroupThroughThePin()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls();
        calls.SendResult = (signal, group) =>
        {
            if (signal == 15)
            {
                child.Kill(); // the leader dies of the TERM; a member stays
            }

            return signal == 0 && group && calls.Calls.Contains("send 9 group") ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent;
        };
        using var handle = Wrap(child, calls);
        calls.Calls.Clear();

        AssertEx.True(handle.Terminate());
        AssertEx.Equal("send 15 group", calls.Calls[0]);
        AssertEx.Equal("send 0 group", calls.Calls[1], "the surviving member is probed through the grace before any KILL");
        AssertEx.Equal("send 9 group,send 9,send 0 group", string.Join(',', calls.Calls.TakeLast(3)));
        AssertEx.Empty(calls.NumericSignals);
        await Task.CompletedTask;
    }

    /// <summary>The grace belongs to the group: a member that finishes shutting down after its leader exited is never KILLed.</summary>
    [Test]
    public async Task TreeKill_WhenTheLeaderExitsOnTermAndTheMemberWithinTheGrace_SendsNoKill()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls();
        var memberProbes = 0;
        calls.SendResult = (signal, group) =>
        {
            if (signal == 15)
            {
                child.Kill(); // the leader dies of the TERM at once
                child.WaitForExit();
                return LinuxPidFdSendStatus.Sent;
            }

            if (!group)
            {
                return child.HasExited ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent;
            }

            // The member answers three probes, then has exited, all well inside the grace.
            return ++memberProbes > 3 ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent;
        };
        using var handle = LinuxProcessGroupHandle.Wrap(child, stderrTail: null, NullLogger.Instance, calls.Open, calls.Send, calls.Kill,
            TimeSpan.FromSeconds(30), Grace);

        AssertEx.True(handle.Terminate());
        AssertEx.True(memberProbes > 3, "the member was waited for");
        AssertEx.False(calls.Calls.Exists(static call => call.StartsWith("send 9", StringComparison.Ordinal)), "no KILL reaches a member that exits in the grace");
        await Task.CompletedTask;
    }

    [Test]
    public async Task TreeKill_WhenTheGroupAndLeaderAreGoneAfterTheGrace_SendsNoKill()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            SendResult = static (signal, _) => signal == 0 ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent
        };
        using var handle = Wrap(child, calls);
        calls.Calls.Clear();

        try
        {
            AssertEx.True(handle.Terminate());
            AssertEx.Equal("send 15 group,send 0 group,send 0", calls.Log());
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task TreeKill_WhenTheGroupStillHasMembersAfterTheKill_IsIncomplete()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls();
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.False(handle.Terminate());
            AssertEx.Contains(calls.Calls, "send 9 group");
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task TreeKill_WhenTheGroupTermFails_SendsNothingElse_AndIsIncomplete()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            SendResult = static (_, _) => LinuxPidFdSendStatus.Failed
        };
        using var handle = Wrap(child, calls);
        calls.Calls.Clear();

        try
        {
            AssertEx.False(handle.Terminate());
            AssertEx.Equal("send 15 group", calls.Log(), "an operational failure never falls back to a numeric signal");
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    /// <summary>Before the child reaches setsid(2) its pid names no group, so the group TERM finds nothing while the leader runs.</summary>
    [Test]
    public async Task TreeKill_GroupGoneWhileLeaderAlive_SignalsTheLeaderThroughThePin()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            SendResult = (signal, group) => group ? LinuxPidFdSendStatus.Gone : KillLeaderOnSigkill(child, signal)
        };
        using var handle = Wrap(child, calls);
        calls.Calls.Clear();

        try
        {
            AssertEx.True(handle.Terminate());
            AssertEx.Equal("send 15 group,send 0,send 15", string.Join(',', calls.Calls.Take(3)), "the leader gets the TERM through the pin");
            AssertEx.Equal("send 9 group,send 9,send 0 group", string.Join(',', calls.Calls.TakeLast(3)));
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    /// <summary>An empty group proves nothing before setsid(2): a leader that outlives its KILL (uninterruptible sleep) keeps the kill incomplete.</summary>
    [Test]
    public async Task TreeKill_GroupGoneWhileLeaderOutlivesItsKill_IsIncomplete()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent
        };
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.False(handle.Terminate(), "the group probe reads Gone while the leader still runs");
            AssertEx.Contains(calls.Calls, "send 9");
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task TreeKill_WithoutTheGroupFlag_SignalsTheGroupByNumberBeforeTheLeader_ThenKills()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            SendResult = (signal, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : KillLeaderOnSigkill(child, signal),
            KillResult = static (_, signal) => signal == 0 ? SandboxSignalCalls.Esrch : 0
        };
        using var handle = Wrap(child, calls);
        calls.Calls.Clear();

        try
        {
            AssertEx.True(handle.Terminate());
            var pgid = child.Id;
            AssertEx.Equal($"send 15 group,send 0,kill -{pgid} 15,send 0,kill -{pgid} 9,send 9,kill -{pgid} 0", calls.Log());
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    /// <summary>Before setsid(2) the numeric group probe finds no group: only the leader's own exit makes the kill complete.</summary>
    [Test]
    public async Task TreeKill_WithoutTheGroupFlag_BeforeSetsid_WhenTheLeaderOutlivesItsKill_IsIncomplete()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent,
            KillResult = static (target, _) => target < 0 ? SandboxSignalCalls.Esrch : 0
        };
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.False(handle.Terminate(), "no group answers while the leader still runs");
            AssertEx.Contains(calls.Calls, "send 9");
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(SandboxSignalCalls.Esrch, true)]
    public async Task TreeKill_WithoutTheGroupFlag_WhenTheLeaderDiesAfterTheTerm_SendsNoKill(int probeErrno, bool complete)
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls();
        calls.SendResult = (_, group) =>
        {
            if (group)
            {
                return LinuxPidFdSendStatus.GroupFlagUnsupported;
            }

            return child.HasExited ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Sent;
        };
        calls.KillResult = (_, signal) =>
        {
            if (signal == 15)
            {
                child.Kill(); // the leader dies of the TERM
                child.WaitForExit();
                return 0;
            }

            return probeErrno;
        };
        using var handle = Wrap(child, calls);

        AssertEx.Equal(complete, handle.Terminate(), "only an empty group is a complete kill");
        AssertEx.Equal($"kill -{child.Id} 15", string.Join(',', calls.NumericSignals), "no KILL may follow the leader's death");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments(LinuxPidFdOpenStatus.Failed)]
    [Arguments(LinuxPidFdOpenStatus.Unsupported)]
    public async Task TreeKill_WithoutAPin_WhileTheChildRuns_TakesTheExitGuardedNumericPath(LinuxPidFdOpenStatus status)
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            OpenStatus = status
        };
        calls.KillResult = (_, signal) => KillOnSigkill(child, signal);
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.True(handle.Terminate());
            var pgid = child.Id;
            AssertEx.Equal($"kill -{pgid} 15,kill -{pgid} 9", string.Join(',', calls.NumericSignals));
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    /// <summary>A child not yet at setsid(2) leads no group: both group signals find nothing while the child runs.</summary>
    [Test]
    public async Task TreeKill_WithoutAPin_BeforeTheChildReachedSetsid_SignalsTheLeaderAndRequiresItsExit()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Unsupported
        };
        calls.KillResult = (target, signal) => target < 0 ? SandboxSignalCalls.Esrch : KillOnSigkill(child, signal);
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.True(handle.Terminate());
            AssertEx.True(child.HasExited, "a Complete kill means the leader is gone, not only its empty group");
            var pid = child.Id;
            AssertEx.Equal($"kill -{pid} 15,kill {pid} 15,kill -{pid} 9,kill {pid} 9", string.Join(',', calls.NumericSignals));
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task TreeKill_WithoutAPin_WhenTheLeaderOutlivesItsKill_IsIncomplete()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Unsupported,
            KillResult = static (target, _) => target < 0 ? SandboxSignalCalls.Esrch : 0
        };
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.False(handle.Terminate(), "an empty group is no proof while the leader still runs");
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task Wrap_WhenTheHostCannotVerifyItsChildren_TakesNoPin_AndUsesTheNumericPath()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls();
        calls.KillResult = (_, signal) => KillOnSigkill(child, signal);
        using var handle = LinuxProcessGroupHandle.Wrap(child, stderrTail: null, NullLogger.Instance, calls.Open, calls.Send, calls.Kill, Grace, Grace,
            hostVerifiable: static () => false);

        try
        {
            AssertEx.True(handle.Terminate());
            AssertEx.False(calls.Calls.Exists(static call => call.StartsWith("open", StringComparison.Ordinal) || call.StartsWith("send", StringComparison.Ordinal)),
                "as PID 1 the runtime may reap before HasExited flips, so no pin is taken or sent through");
            var pgid = child.Id;
            AssertEx.Equal($"kill -{pgid} 15,kill -{pgid} 9", string.Join(',', calls.NumericSignals));
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    /// <summary>Scripted kill(2): a SIGKILL really kills the child, any other signal is accepted without effect, a probe finds no group.</summary>
    private static int KillOnSigkill(Process child, int signal)
    {
        if (signal == 0)
        {
            return SandboxSignalCalls.Esrch;
        }

        if (signal == 9)
        {
            child.Kill();
            child.WaitForExit();
        }

        return 0;
    }

    /// <summary>Scripted leader send through the pin: a SIGKILL really kills the child, anything else is accepted without effect.</summary>
    private static LinuxPidFdSendStatus KillLeaderOnSigkill(Process child, int signal)
    {
        if (signal == 9)
        {
            child.Kill();
            child.WaitForExit();
        }

        return LinuxPidFdSendStatus.Sent;
    }

    [Test]
    public async Task TreeKill_WithoutAPin_WhenTheNumericTermFails_IsIncomplete()
    {
        using var child = Start("exec sleep 30");
        var calls = new SandboxSignalCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Failed,
            KillResult = static (_, _) => 1
        };
        using var handle = Wrap(child, calls);

        try
        {
            AssertEx.False(handle.Terminate());
            AssertEx.Equal($"kill -{child.Id} 15", string.Join(',', calls.NumericSignals));
        }
        finally
        {
            child.Kill();
        }

        await Task.CompletedTask;
    }

    private static LinuxProcessGroupHandle Wrap(Process child, SandboxSignalCalls calls) =>
        LinuxProcessGroupHandle.Wrap(child, stderrTail: null, NullLogger.Instance, calls.Open, calls.Send, calls.Kill, Grace, Grace);

    /// <summary>A real <c>/bin/sh -c</c> child, not under setsid: every signal to its "group" is scripted, never sent.</summary>
    private static Process Start(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return Process.Start(startInfo) ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{script} did not start."));
    }
}
