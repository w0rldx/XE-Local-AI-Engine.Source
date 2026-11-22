namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A scripted <c>/proc</c> and pidfd layer for <see cref="LinuxProcessTreeKill" />: nothing reaches the kernel.</summary>
/// <remarks>Fds never own a real handle. <see cref="Log" /> holds "open 10", "list", "stop 10", "probe 10", "kill 10", "sig 18 10" in call order.</remarks>
internal sealed class FakeProcessTable
{
    private const int Sigkill = 9;
    private const int Sigstop = 19;
    private readonly Dictionary<LinuxPidFd, FakeTreeProcess> _pinned = [];
    private readonly Dictionary<int, FakeTreeProcess> _processes = [];

    public List<string> Log { get; } = [];

    public List<LinuxPidFd> Opened { get; } = [];

    public ManualTimeProvider Time { get; } = new();

    /// <summary>Runs before each call with its name ("open 11", "stat 11", "list", "kill 11", ...): a test throws, forks or advances time here.</summary>
    public Action<string> BeforeCall { get; set; } = static _ => { };

    public bool HostVerifiable { get; set; } = true;

    public int MaxMembers { get; set; } = 512;

    public FakeTreeProcess this[int pid] => _processes[pid];

    public FakeTreeProcess Add(int pid, int parentPid, long startTicks = 1000, List<string>? threads = null, int? unstoppableThread = null)
    {
        var process = new FakeTreeProcess
        {
            Pid = pid,
            ParentPid = parentPid,
            StartTicks = startTicks,
            ProcessGroupId = pid,
            Threads = threads ?? ["S"],
            UnstoppableThread = unstoppableThread
        };
        _processes[pid] = process;
        return process;
    }

    /// <summary>Kills the tree rooted at <paramref name="rootPid" />; the fallback only logs "fallback" and reports FallbackKilled.</summary>
    public LinuxProcessTreeKillResult Kill(int rootPid, Func<bool>? rootExited = null) =>
        LinuxProcessTreeKill.Kill(rootPid, rootExited ?? (() => false), () =>
        {
            Log.Add("fallback");
            return LinuxProcessTreeKillResult.FallbackKilled;
        }, new LinuxProcessTreeKillCalls
        {
            HostVerifiable = () => HostVerifiable,
            Open = Open,
            Send = Send,
            ListProcesses = List,
            ReadStat = ReadStat,
            ListThreads = ListThreads,
            ReadThreadStat = ReadThreadStat,
            WaitUntil = static (condition, _) => condition(),
            Time = Time,
            MaxMembers = MaxMembers
        });

    /// <summary>The walk's safety net, asserted after every kill: no SIGCONT, every stopped process got a later SIGKILL, every fd closed.</summary>
    public void AssertCleanup()
    {
        foreach (var process in _processes.Values.Concat(_processes.Values.Select(static process => process.ReplacedBeforeOpen).OfType<FakeTreeProcess>()))
        {
            AssertEx.False(process.Signals.Contains(18), $"pid {process.Pid} got SIGCONT");
            var stop = process.Signals.IndexOf(Sigstop);
            AssertEx.True(stop < 0 || process.Signals.LastIndexOf(Sigkill) > stop, $"pid {process.Pid} was stopped and never killed");
        }

        AssertEx.True(Opened.TrueForAll(static pidFd => pidFd.IsClosed), "an opened pidfd was left open");
    }

    private LinuxPidFd Open(int pid)
    {
        BeforeCall($"open {pid}");
        Log.Add($"open {pid}");
        if (_processes.TryGetValue(pid, out var process) && process.ReplacedBeforeOpen is { } replacement)
        {
            process.Reaped = true;
            _processes[pid] = replacement;
            process = replacement;
        }

        var status = process is null || process.Reaped ? LinuxPidFdOpenStatus.Gone : process.OpenStatus;
        var pidFd = new LinuxPidFd(status == LinuxPidFdOpenStatus.Opened ? 4096 + Opened.Count : -1, ownsHandle: false, status);
        Opened.Add(pidFd);
        if (status == LinuxPidFdOpenStatus.Opened)
        {
            _pinned[pidFd] = process!;
            if (process!.ReparentedOnOpen)
            {
                process.ParentPid = 1;
            }
        }

        return pidFd;
    }

    private LinuxPidFdSendResult Send(LinuxPidFd pidFd, int signal)
    {
        var process = _pinned[pidFd];
        var name = signal switch
        {
            0 => "probe",
            Sigstop => "stop",
            Sigkill => "kill",
            _ => $"sig {signal}"
        };
        BeforeCall($"{name} {process.Pid}");
        Log.Add($"{name} {process.Pid}");
        AssertEx.False(pidFd.IsClosed, $"a {name} went through a closed fd");
        if (process.SendOverride(signal) is { } status)
        {
            return new LinuxPidFdSendResult(status, process.SendErrno);
        }

        if (process.Reaped)
        {
            return new LinuxPidFdSendResult(LinuxPidFdSendStatus.Gone, Errno: 0);
        }

        if (signal != 0)
        {
            process.Signals.Add(signal);
        }

        if (signal == Sigstop)
        {
            for (var index = 0; index < process.Threads.Count; index++)
            {
                if (index != process.UnstoppableThread && process.Threads[index] is "S" or "R")
                {
                    process.Threads[index] = "T";
                }
            }
        }
        else if (signal == Sigkill)
        {
            process.Reaped = true;
        }

        return new LinuxPidFdSendResult(LinuxPidFdSendStatus.Sent, Errno: 0);
    }

    private List<int> List()
    {
        BeforeCall("list");
        Log.Add("list");
        return [.. _processes.Values.Where(static process => !process.Reaped).Select(static process => process.Pid)];
    }

    private LinuxProcStat ReadStat(int pid)
    {
        BeforeCall($"stat {pid}");
        if (!_processes.TryGetValue(pid, out var process) || process.Reaped || process.StatOutcome == LinuxProcStatOutcome.Missing)
        {
            return LinuxProcStat.Missing;
        }

        return process.StatOutcome == LinuxProcStatOutcome.Unreadable ? LinuxProcStat.Unreadable : StatOf(process, process.Threads[0]);
    }

    private List<int>? ListThreads(int pid) =>
        _processes.TryGetValue(pid, out var process) && !process.Reaped
            ? [.. Enumerable.Range(0, process.Threads.Count).Select(index => (pid * 100) + index)]
            : null;

    private LinuxProcStat ReadThreadStat(int pid, int tid) =>
        _processes.TryGetValue(pid, out var process) && !process.Reaped ? StatOf(process, process.Threads[tid - (pid * 100)]) : LinuxProcStat.Missing;

    private static LinuxProcStat StatOf(FakeTreeProcess process, string state) =>
        new()
        {
            Outcome = LinuxProcStatOutcome.Parsed,
            ParentProcessId = process.ParentPid,
            ProcessGroupId = process.ProcessGroupId,
            StartTicks = process.StartTicks,
            Exited = state is "Z" or "X",
            Stopped = state is "T"
        };
}
