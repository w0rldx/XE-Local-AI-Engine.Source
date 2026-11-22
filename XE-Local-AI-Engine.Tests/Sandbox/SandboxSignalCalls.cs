namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Scripted pidfd calls, <c>kill(2)</c> and stat reads for <see cref="LinuxSandboxProcessGroupKiller" />; nothing reaches the kernel.</summary>
/// <remarks>Calls are logged in order as "open 4242", "send 15 group", "send 0", "kill -4242 9", "tree".</remarks>
internal sealed class SandboxSignalCalls
{
    public const int Esrch = 3;

    public List<string> Calls { get; } = [];

    public LinuxPidFdOpenStatus OpenStatus { get; init; } = LinuxPidFdOpenStatus.Opened;

    /// <summary>What each send answers, given the signal and whether it targets the group; sent by default.</summary>
    public Func<int, bool, LinuxPidFdSendStatus> SendResult { get; set; } = static (_, _) => LinuxPidFdSendStatus.Sent;

    /// <summary>What <c>kill(target, signal)</c> answers: 0, else the errno. ESRCH (an empty group) by default.</summary>
    public Func<int, int, int> KillResult { get; set; } = static (_, _) => Esrch;

    /// <summary>The leader's stat; parsed with start ticks 99 and leading group 4242 by default.</summary>
    public Func<int, LinuxProcStat> Stat { get; set; } = static _ => Parsed(startTicks: 99);

    /// <summary>The non-zero numeric signals sent: what a pinned or deferred path must never send.</summary>
    public IEnumerable<string> NumericSignals => Calls.Where(static call => call.StartsWith("kill ", StringComparison.Ordinal) && !call.EndsWith(" 0", StringComparison.Ordinal));

    public static LinuxProcStat Parsed(long startTicks, int processGroupId = 4242, bool exited = false) =>
        new()
        {
            Outcome = LinuxProcStatOutcome.Parsed,
            ParentProcessId = 1,
            ProcessGroupId = processGroupId,
            StartTicks = startTicks,
            Exited = exited
        };

    public LinuxSandboxProcessGroupKiller Killer(TimeProvider time) =>
        new(time, Open, Send, Kill, pid => Stat(pid), ownedProbeBound: TimeSpan.Zero);

    /// <summary>A pin as a launch would hold it, opened through this script.</summary>
    public LinuxPidFd Open(int pid)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"open {pid}"));
        // A made-up fd the handle never owns, so disposing it closes nothing real.
        return new LinuxPidFd(OpenStatus == LinuxPidFdOpenStatus.Opened ? 4096 : -1, ownsHandle: false, OpenStatus);
    }

    public LinuxPidFdSendResult Send(LinuxPidFd pidFd, int signal, bool group)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"send {signal}{(group ? " group" : "")}"));
        return new LinuxPidFdSendResult(SendResult(signal, group), Errno: 0);
    }

    public int Kill(int target, int signal)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"kill {target} {signal}"));
        return KillResult(target, signal);
    }

    /// <summary>What the scripted tree kill reports: false when a descendant that left the group may survive.</summary>
    public bool TreeComplete { get; init; } = true;

    public bool Tree()
    {
        Calls.Add("tree");
        return TreeComplete;
    }

    public string Log() =>
        string.Join(',', Calls);
}
