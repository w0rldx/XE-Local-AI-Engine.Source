// The Desktop shell compiles this file as linked source under its own namespace (it has no engine references).

#if XE_DESKTOP
namespace XE_Local_AI_Engine.Desktop.Linux;
#else
namespace XE_Local_AI_Engine.Providers.Abstractions;
#endif

using System.ComponentModel;
using System.Diagnostics;

/// <summary>Kills a process this host started via <see cref="Process.Start()" />, and its descendants; never pass a <c>GetProcessById</c> result.</summary>
/// <remarks>
///     On Linux every process is pinned by pidfd and verified before it is stopped, children are found by ppid, and SIGKILL goes leaves first,
///     root last; never SIGCONT, never a numeric signal except the runtime fallback. Never throws there. The owned-root proof (open, then
///     <c>!HasExited</c>) assumes the .NET runtime is this host's only reaper. Off Linux it is exactly <c>Process.Kill(entireProcessTree: true)</c>.
/// </remarks>
public static class LinuxProcessTreeKill
{
    private const int Sigkill = 9;
    private const int Sigstop = 19;

    /// <summary>Whether an owned-child pin may be trusted here: the host is not PID 1 and <c>/proc</c> numbers its own PID namespace.</summary>
    /// <remarks>As PID 1 the runtime reaps every child with <c>waitpid(-1)</c>, which can reap before <c>HasExited</c> flips.</remarks>
    public static bool IsHostVerifiable() =>
        LinuxProcessTreeKillCalls.Real.HostVerifiable();

    public static LinuxProcessTreeKillResult Kill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsLinux())
        {
            process.Kill(entireProcessTree: true);
            return LinuxProcessTreeKillResult.FallbackKilled;
        }

        int rootPid;
        try
        {
            rootPid = process.Id;
        }
        catch (InvalidOperationException)
        {
            // Never started or already disposed: nothing this call could own.
            return LinuxProcessTreeKillResult.Complete;
        }

        return Kill(rootPid, () => HasExited(process),
            () => RuntimeTreeKill(() => process.HasExited, () => process.Kill(entireProcessTree: true)), LinuxProcessTreeKillCalls.Real);
    }

    /// <summary>The walk behind <see cref="Kill(Process)" />: <paramref name="rootExited" /> is read after the pin, <paramref name="fallback" /> runs when no pin can be trusted or signalled through.</summary>
    internal static LinuxProcessTreeKillResult Kill(int rootPid,
        Func<bool> rootExited,
        Func<LinuxProcessTreeKillResult> fallback,
        LinuxProcessTreeKillCalls calls)
    {
        try
        {
            if (!calls.HostVerifiable())
            {
                return fallback();
            }

            var root = calls.Open(rootPid);
            var status = root.OpenStatus;
            if (status != LinuxPidFdOpenStatus.Opened)
            {
                root.Dispose();
                return status == LinuxPidFdOpenStatus.Gone ? LinuxProcessTreeKillResult.Complete : fallback();
            }

            var walk = new TreeWalk(calls);
            var result = walk.Run(rootPid, root, rootExited);
            return walk.RootSendRefused ? fallback() : result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return LinuxProcessTreeKillResult.Incomplete;
        }
    }

    /// <summary>The runtime's own (numeric) tree kill, mapped to a result: an exited root is Complete, a kill error Incomplete.</summary>
    internal static LinuxProcessTreeKillResult RuntimeTreeKill(Func<bool> hasExited, Action killTree)
    {
        try
        {
            if (hasExited())
            {
                return LinuxProcessTreeKillResult.Complete;
            }

            killTree();
            return LinuxProcessTreeKillResult.FallbackKilled;
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
            return LinuxProcessTreeKillResult.Complete;
        }
        catch (Exception exception) when (exception is Win32Exception or AggregateException or NotSupportedException)
        {
            return LinuxProcessTreeKillResult.Incomplete;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed class Member
    {
        public required LinuxPidFd Fd { get; init; }

        public required int Pid { get; init; }

        public required long StartTicks { get; init; }

        /// <summary>False once a send read Gone or its identity is unknown: it then parents nothing in the walk.</summary>
        public bool Live { get; set; } = true;
    }

    private sealed class TreeWalk
    {
        private readonly LinuxProcessTreeKillCalls _calls;
        private readonly long _deadline;
        private readonly Dictionary<int, long> _handled = [];
        private readonly List<Member> _members = [];
        private bool _incomplete;

        public TreeWalk(LinuxProcessTreeKillCalls calls)
        {
            _calls = calls;
            _deadline = calls.Time.GetTimestamp() + (long)(calls.TotalBound.TotalSeconds * calls.Time.TimestampFrequency);
        }

        private bool Expired => _calls.Time.GetTimestamp() >= _deadline;

        /// <summary>A filter allowed <c>pidfd_open</c> but refused <c>pidfd_send_signal</c> on the root, so nothing could be sent through any pin.</summary>
        public bool RootSendRefused { get; private set; }

        /// <summary>Walks, then SIGKILLs every member on every exit, an <see cref="OutOfMemoryException" /> included; the result is chosen after that cleanup.</summary>
        public LinuxProcessTreeKillResult Run(int rootPid, LinuxPidFd rootFd, Func<bool> rootExited)
        {
            var killFailed = false;
            try
            {
                Walk(rootPid, rootFd, rootExited);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _incomplete = true;
            }
            finally
            {
                killFailed = KillMembers();
            }

            if (killFailed)
            {
                return LinuxProcessTreeKillResult.KillFailed;
            }

            return _incomplete ? LinuxProcessTreeKillResult.Incomplete : LinuxProcessTreeKillResult.Complete;
        }

        private void Walk(int rootPid, LinuxPidFd rootFd, Func<bool> rootExited)
        {
            var pending = rootFd;
            try
            {
                if (rootExited())
                {
                    // Reaped since the open, so the fd may name a recycled pid.
                    return;
                }

                var stat = _calls.ReadStat(rootPid);
                if (stat.Outcome != LinuxProcStatOutcome.Parsed)
                {
                    // Pinned and owned, but unidentifiable: it is killed through its fd, never by number, and parents nothing.
                    Adopt(ref pending, rootPid, startTicks: 0).Live = false;
                    _incomplete = stat.Outcome == LinuxProcStatOutcome.Unreadable || !rootExited();
                    return;
                }

                var probe = _calls.Send(pending, 0);
                // EPERM, EACCES or ENOSYS on this host's own child can only be a filter, as for an open that reads Unsupported.
                if (probe.Status == LinuxPidFdSendStatus.Failed && LinuxPidFd.OpenStatusOf(probe.Errno) == LinuxPidFdOpenStatus.Unsupported)
                {
                    RootSendRefused = true;
                    return;
                }

                if (IsDead(probe, rootPid))
                {
                    return;
                }

                _handled[rootPid] = stat.StartTicks;
                StopAndConfirm(Adopt(ref pending, rootPid, stat.StartTicks));
            }
            finally
            {
                pending?.Dispose();
            }

            Discover();
        }

        /// <summary>Scans rounds until one finds no new child; rounds are sequential, so each starts after every earlier stop attempt finished.</summary>
        private void Discover()
        {
            var unreadableRetry = true;
            while (true)
            {
                var parents = new Dictionary<int, Member>();
                foreach (var member in _members.Where(static member => member.Live))
                {
                    parents[member.Pid] = member;
                }

                if (parents.Count == 0)
                {
                    return;
                }

                var admitted = 0;
                var sawUnreadable = false;
                foreach (var pid in _calls.ListProcesses())
                {
                    if (Expired)
                    {
                        _incomplete = true;
                        return;
                    }

                    if (parents.ContainsKey(pid))
                    {
                        continue;
                    }

                    var stat = _calls.ReadStat(pid);
                    sawUnreadable |= stat.Outcome == LinuxProcStatOutcome.Unreadable;
                    if (stat.Outcome != LinuxProcStatOutcome.Parsed
                        || !parents.TryGetValue(stat.ParentProcessId, out var parent)
                        || (_handled.TryGetValue(pid, out var seen) && seen == stat.StartTicks))
                    {
                        continue;
                    }

                    if (_members.Count >= _calls.MaxMembers)
                    {
                        _incomplete = true;
                        return;
                    }

                    _handled[pid] = stat.StartTicks;
                    if (Admit(pid, stat.StartTicks, parent) is { } child)
                    {
                        admitted++;
                        StopAndConfirm(child);
                        if (child.Live)
                        {
                            parents[pid] = child;
                        }
                    }
                }

                // An unreadable entry may be a child that is just starting: one more round, then it is not attributable.
                if (admitted == 0 && !(sawUnreadable && unreadableRetry))
                {
                    return;
                }

                unreadableRetry &= admitted != 0;
            }
        }

        /// <summary>Pins a scanned child and verifies it AFTER the open: a foreign process under that pid fails the check, a dead one is harmless.</summary>
        private Member? Admit(int pid, long scanTicks, Member parent)
        {
            var pending = _calls.Open(pid);
            try
            {
                if (pending.OpenStatus != LinuxPidFdOpenStatus.Opened)
                {
                    _incomplete |= pending.OpenStatus != LinuxPidFdOpenStatus.Gone;
                    return null;
                }

                var stat = _calls.ReadStat(pid);
                if (stat.Outcome != LinuxProcStatOutcome.Parsed)
                {
                    _incomplete |= stat.Outcome == LinuxProcStatOutcome.Unreadable;
                    return null;
                }

                if (stat.ParentProcessId != parent.Pid)
                {
                    // Same start ticks: the scanned child itself left the tree (re-parented). Otherwise its pid was reused.
                    _incomplete |= stat.StartTicks == scanTicks;
                    return null;
                }

                if (stat.StartTicks < parent.StartTicks)
                {
                    _incomplete = true;
                    return null;
                }

                if (IsDead(pending, pid))
                {
                    return null;
                }

                // After the stat read: a parent still unreaped then held its pid, so the ppid named it.
                if (_calls.Send(parent.Fd, 0).Status != LinuxPidFdSendStatus.Sent)
                {
                    _incomplete = true;
                    return null;
                }

                return Adopt(ref pending, pid, stat.StartTicks);
            }
            finally
            {
                pending?.Dispose();
            }
        }

        /// <summary>Moves an fd into the members BEFORE any signal, so the cleanup owns it from here on.</summary>
        private Member Adopt(ref LinuxPidFd? pending, int pid, long startTicks)
        {
            var member = new Member
            {
                Fd = pending!,
                Pid = pid,
                StartTicks = startTicks
            };
            _members.Add(member);
            pending = null;
            return member;
        }

        private void StopAndConfirm(Member member)
        {
            var status = _calls.Send(member.Fd, Sigstop).Status;
            if (status == LinuxPidFdSendStatus.Gone)
            {
                member.Live = false;
                return;
            }

            if (status != LinuxPidFdSendStatus.Sent)
            {
                _incomplete = true;
                return;
            }

            var left = _calls.Time.GetElapsedTime(_calls.Time.GetTimestamp(), _deadline);
            var timeout = left < _calls.StopConfirmBound ? left : _calls.StopConfirmBound;
            if (!_calls.WaitUntil(() => IsStopped(member), timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout))
            {
                _incomplete = true;
            }
        }

        private bool IsStopped(Member member)
        {
            if (_calls.Send(member.Fd, 0).Status == LinuxPidFdSendStatus.Gone)
            {
                member.Live = false;
                return true;
            }

            return ReadThreads(member.Pid) is { } threads && threads.TrueForAll(static thread => thread.Stopped || thread.Exited);
        }

        private bool IsDead(LinuxPidFd pidFd, int pid) =>
            IsDead(_calls.Send(pidFd, 0), pid);

        private bool IsDead(LinuxPidFdSendResult probe, int pid) =>
            probe.Status == LinuxPidFdSendStatus.Gone
            || (ReadThreads(pid) is { } threads && threads.TrueForAll(static thread => thread.Exited));

        /// <summary>Every thread's stat, or null when the listing is missing, empty, partial or changed between two reads.</summary>
        private List<LinuxProcStat>? ReadThreads(int pid)
        {
            if (_calls.ListThreads(pid) is not { Count: > 0 } tids)
            {
                return null;
            }

            var threads = new List<LinuxProcStat>(tids.Count);
            foreach (var tid in tids)
            {
                var stat = _calls.ReadThreadStat(pid, tid);
                if (Expired || stat.Outcome != LinuxProcStatOutcome.Parsed)
                {
                    return null;
                }

                threads.Add(stat);
            }

            return _calls.ListThreads(pid) is { } again && new HashSet<int>(tids).SetEquals(again) ? threads : null;
        }

        /// <summary>SIGKILL to every member, leaves first and the root last, each isolated; true when any refused it.</summary>
        private bool KillMembers()
        {
            var failed = false;
            for (var index = _members.Count - 1; index >= 0; index--)
            {
                var member = _members[index];
                try
                {
                    failed |= _calls.Send(member.Fd, Sigkill).Status is LinuxPidFdSendStatus.Failed or LinuxPidFdSendStatus.GroupFlagUnsupported;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    failed = true;
                }
                finally
                {
                    member.Fd.Dispose();
                }
            }

            return failed;
        }
    }
}
