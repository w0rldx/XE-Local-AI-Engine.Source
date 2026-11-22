namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>One scripted process in a <see cref="FakeProcessTable" />: its stat fields, its threads, and every signal its pidfd received.</summary>
internal sealed class FakeTreeProcess
{
    public required int Pid { get; init; }

    public int ParentPid { get; set; }

    public long StartTicks { get; set; } = 1000;

    public int ProcessGroupId { get; set; }

    /// <summary>Thread states, leader first ("S", "R", "t", "Z", ...); a SIGSTOP turns every running state into "T".</summary>
    public List<string> Threads { get; init; } = ["S"];

    /// <summary>A thread (by index) that a SIGSTOP never reaches, like one in uninterruptible sleep.</summary>
    public int? UnstoppableThread { get; init; }

    public LinuxPidFdOpenStatus OpenStatus { get; set; } = LinuxPidFdOpenStatus.Opened;

    public LinuxProcStatOutcome StatOutcome { get; set; } = LinuxProcStatOutcome.Parsed;

    /// <summary>What a send of a given signal answers instead of the simulation; null keeps the simulation.</summary>
    public Func<int, LinuxPidFdSendStatus?> SendOverride { get; set; } = static _ => null;

    /// <summary>The errno an overridden send reports; 0 by default.</summary>
    public int SendErrno { get; set; }

    /// <summary>Another process that takes this pid just before an open, so the fd pins it (pid reuse after the scan).</summary>
    public FakeTreeProcess? ReplacedBeforeOpen { get; set; }

    /// <summary>The process is re-parented to init just after an open, keeping its start ticks.</summary>
    public bool ReparentedOnOpen { get; set; }

    public bool Reaped { get; set; }

    /// <summary>Every signal sent through an fd that pins THIS process, in order.</summary>
    public List<int> Signals { get; } = [];
}
