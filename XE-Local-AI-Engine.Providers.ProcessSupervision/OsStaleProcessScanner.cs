namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Production <see cref="IStaleProcessScanner" />: reads the OS process table for processes with the provider's
///     runtime process name and tree-kills by pid via <see cref="Process" />.
/// </summary>
/// <remarks>
///     On Linux the executable path is read from the <c>/proc/&lt;pid&gt;/exe</c> symlink, the kernel's authoritative
///     pointer to the real binary, which is more reliable than <see cref="ProcessModule.FileName" /> — that can throw
///     for a foreign-owned or just-exited process. Other platforms read <see cref="Process.MainModule" />.
/// </remarks>
public sealed class OsStaleProcessScanner : IStaleProcessScanner
{
    private const int Sigkill = 9;
    private const int Esrch = 3;

    private readonly Func<int, LinuxPidFd> _openPidFd;
    private readonly string _processName;
    private readonly Func<int, string?> _readExecutablePath;
    private readonly Func<int, ProcessStat?> _readStat;
    private readonly Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> _sendPidFd;
    private readonly Func<int, int, int> _signal;

    /// <summary>
    ///     Scans for <paramref name="processName" />, which carries no extension on any platform (Windows reports
    ///     <c>llama-server</c>, not <c>llama-server.exe</c>).
    /// </summary>
    public OsStaleProcessScanner(string processName)
        : this(processName, LinuxPidFd.Open, static (pidFd, signal, group) => pidFd.Send(signal, group), LinuxProcFs.ReadStatOrNull,
            LinuxProcFs.ReadExecutablePathOrNull, LinuxProcFs.Signal)
    {
    }

    /// <summary>Seam for the receipt kill: the pidfd calls, the <c>/proc</c> reads and <c>kill(2)</c> are swapped out by tests.</summary>
    /// <param name="signal"><c>kill(2)</c>: 0 once the kernel accepted the signal, else the errno.</param>
    internal OsStaleProcessScanner(string processName,
        Func<int, LinuxPidFd> openPidFd,
        Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> sendPidFd,
        Func<int, ProcessStat?> readStat,
        Func<int, string?> readExecutablePath,
        Func<int, int, int> signal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        _processName = processName;
        _openPidFd = openPidFd;
        _sendPidFd = sendPidFd;
        _readStat = readStat;
        _readExecutablePath = readExecutablePath;
        _signal = signal;
    }

    /// <inheritdoc />
    public IReadOnlyList<StaleProcess> EnumerateProcesses()
    {
        var results = new List<StaleProcess>();

        // Matches every process of that name, ours and unrelated ones (another application's llama-server); the reaper's path filter
        // excludes the latter. A process-table read failure surfaces nothing rather than throwing (best-effort).
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(_processName);
        }
        catch (InvalidOperationException)
        {
            return results;
        }

        foreach (var process in processes)
        {
            try
            {
                results.Add(new StaleProcess(process.Id, ResolveExecutablePath(process)));
            }
            catch (InvalidOperationException)
            {
                // The process exited between enumeration and the id/path read — skip it, never abort the whole scan.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    /// <inheritdoc />
    public void KillProcessTree(int pid)
    {
        // Best-effort: the pid may have exited between enumeration and here, or be foreign-owned (access denied). All of
        // these are non-fatal for a startup reaper, so swallow the specific failures and let the caller log the attempt.
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // No such process — already gone.
        }
        catch (InvalidOperationException)
        {
            // Exited between the lookup and the kill.
        }
        catch (Win32Exception)
        {
            // Access denied / the OS refused the kill.
        }
        catch (NotSupportedException)
        {
            // Platform without tree-kill support.
        }
    }

    /// <inheritdoc />
    public ProcessStat? ReadStat(int pid) =>
        LinuxProcFs.ReadStatOrNull(pid);

    /// <inheritdoc />
    /// <remarks>
    ///     The fd names whoever held the pid at the open, so identity read after it binds the fd to the recorded process and a later reuse only
    ///     makes the send fail. A read that fails while the fd's process lives is unreadable, never a mismatch. Where pidfd is unsupported, the
    ///     identity is read and the pid signalled by number, leaving a microsecond window.
    /// </remarks>
    public StaleProcessKillResult KillIfSameProcess(int pid, string expectedExecutablePath, long expectedStartTicks)
    {
        // Never pid 0/1/-1 (caller's group, init, everything) and never this host.
        if (pid <= 1 || pid == Environment.ProcessId)
        {
            return StaleProcessKillResult.Mismatch;
        }

        using var pidFd = _openPidFd(pid);
        switch (pidFd.OpenStatus)
        {
            case LinuxPidFdOpenStatus.Gone:
                return StaleProcessKillResult.Mismatch;
            case LinuxPidFdOpenStatus.Failed:
                return StaleProcessKillResult.Deferred;
            case LinuxPidFdOpenStatus.Unsupported:
                return KillByNumberIfSameProcess(pid, expectedExecutablePath, expectedStartTicks);
        }

        if (_readStat(pid) is not { } stat)
        {
            return UnreadableUnlessGone(pidFd);
        }

        // Read after the open, so a preliminary read that failed cannot let this host's own live server through.
        if (stat.ParentProcessId == Environment.ProcessId)
        {
            return StaleProcessKillResult.OwnChild;
        }

        // A zombie runs nothing and its exe link reads nothing: the receipt names no server any more.
        if (stat.StartTicks != expectedStartTicks || stat.Exited)
        {
            return StaleProcessKillResult.Mismatch;
        }

        if (_readExecutablePath(pid) is not { } executablePath)
        {
            return UnreadableUnlessGone(pidFd);
        }

        if (!string.Equals(executablePath, expectedExecutablePath, StringComparison.Ordinal))
        {
            return StaleProcessKillResult.Mismatch;
        }

        // A server launched under setsid leads its own group, so the group signal reaps any child it forked; one that does not lead a group
        // shares it with something else, so only its pid is safe.
        var group = stat.ProcessGroupId == pid;
        var sent = _sendPidFd(pidFd, Sigkill, group).Status;
        if (sent == LinuxPidFdSendStatus.GroupFlagUnsupported)
        {
            // Probe-then-kill(-pgid) where the kernel refuses the group flag: the leader could die and its emptied group be reused in between.
            sent = _sendPidFd(pidFd, 0, false).Status;
            if (sent == LinuxPidFdSendStatus.Sent)
            {
                // Only ESRCH proves the group empty; a refusal (EPERM) says nothing about who holds the pid.
                return _signal(-pid, Sigkill) switch
                {
                    0 => StaleProcessKillResult.Killed,
                    Esrch => StaleProcessKillResult.Mismatch,
                    _ => StaleProcessKillResult.Deferred
                };
            }
        }

        return sent switch
        {
            LinuxPidFdSendStatus.Sent => StaleProcessKillResult.Killed,
            LinuxPidFdSendStatus.Gone => StaleProcessKillResult.Mismatch,
            _ => StaleProcessKillResult.Deferred
        };
    }

    /// <summary>A failed <c>/proc</c> read proves nothing while the fd's process still lives; once it is reaped the receipt names no process.</summary>
    private StaleProcessKillResult UnreadableUnlessGone(LinuxPidFd pidFd) =>
        _sendPidFd(pidFd, 0, false).Status switch
        {
            LinuxPidFdSendStatus.Sent => StaleProcessKillResult.Unreadable,
            LinuxPidFdSendStatus.Gone => StaleProcessKillResult.Mismatch,
            _ => StaleProcessKillResult.Deferred
        };

    /// <summary>The check-then-kill where the kernel offers no pidfd: the same reads, order and outcomes the receipt reap always had.</summary>
    private StaleProcessKillResult KillByNumberIfSameProcess(int pid, string expectedExecutablePath, long expectedStartTicks)
    {
        if (!string.Equals(_readExecutablePath(pid), expectedExecutablePath, StringComparison.Ordinal)
            || _readStat(pid) is not { } stat
            || stat.StartTicks != expectedStartTicks)
        {
            return StaleProcessKillResult.Mismatch;
        }

        if (stat.ParentProcessId == Environment.ProcessId)
        {
            return StaleProcessKillResult.OwnChild;
        }

        var target = stat.ProcessGroupId == pid ? -pid : pid;
        return _signal(target, Sigkill) == 0 ? StaleProcessKillResult.Killed : StaleProcessKillResult.Mismatch;
    }

    private static string? ResolveExecutablePath(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            // /proc/<pid>/exe is a symlink to the real binary; resolving it avoids ProcessModule.FileName, which can
            // throw for a process this user does not own.
            return LinuxProcFs.ReadExecutablePathOrNull(process.Id);
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
