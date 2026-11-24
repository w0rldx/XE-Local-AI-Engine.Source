namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Linux process handle whose tree-kill signals the child's whole process group.
/// </summary>
/// <remarks>
///     The caller starts the child under <c>setsid</c>, so its pid is also its process-group id. The leader is pinned by pidfd at the
///     wrap, and the group is signalled through that pin (Linux 6.9), which still reaches the members after the leader died and never a
///     process that reused its number. Polite first: <c>SIGTERM</c>, a two-second grace for the group, then <c>SIGKILL</c>; it counts as
///     complete only once the leader has exited and the group is seen empty. Without the pin the numeric <c>kill(-pgid)</c> is sent only while the leader lives.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LinuxProcessGroupHandle : IProcessTreeHandle
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Esrch = 3;

    private static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(2);

    // Applied twice after a KILL (leader exit, then the group check): at most ~2 s past the grace, never under the sandbox registry lock,
    // and long enough for init to reap a SIGKILLed member before the kill is reported incomplete.
    private static readonly TimeSpan DefaultKillVerifyBound = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _grace;
    private readonly TimeSpan _killVerifyBound;
    private readonly ILogger _logger;
    private readonly LinuxPidFd? _pin;
    private readonly LinuxPidFdOpenStatus _pinStatus;
    private readonly int _processGroupId;
    private readonly Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> _sendPidFd;
    private readonly Func<int, int, int> _signal;
    private readonly ProcessStderrTail? _stderrTail;
    private int _disposed;

    private LinuxProcessGroupHandle(Process process,
        int processGroupId,
        ProcessStderrTail? stderrTail,
        ILogger logger,
        LinuxPidFd? pin,
        LinuxPidFdOpenStatus pinStatus,
        Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> sendPidFd,
        Func<int, int, int> signal,
        TimeSpan grace,
        TimeSpan killVerifyBound)
    {
        Process = process;
        _processGroupId = processGroupId;
        _stderrTail = stderrTail;
        _logger = logger;
        _pin = pin;
        _pinStatus = pinStatus;
        _sendPidFd = sendPidFd;
        _signal = signal;
        _grace = grace;
        _killVerifyBound = killVerifyBound;
    }

    /// <summary>The wrapped process, for run-to-completion runners that await exit and read the code while the handle owns disposal and tree-kill.</summary>
    public Process Process { get; }

    public int ProcessId => Process.Id;

    public bool HasExited => ProcessExitState.SafeHasExited(Process);

    public int? ExitCode => ProcessExitState.SafeExitCode(Process);

    public string? StderrTail => _stderrTail?.Snapshot();

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) =>
        ProcessExitState.WaitForExitAsync(Process, timeout, ct);

    public void TreeKill() =>
        _ = Terminate();

    /// <summary>Kills the group; <see langword="true" /> once the leader exited and the group is seen empty, otherwise logs one Warning naming why not.</summary>
    internal bool Terminate()
    {
        var incomplete = _pin is { } pin ? TerminatePinned(pin) : TerminateUnpinned();
        if (incomplete is null)
        {
            return true;
        }

        _logger.LogWarning("Process group {Pgid} may still have members after its tree kill: {Reason}.", _processGroupId, incomplete);
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        try
        {
            TreeKill();
        }
        finally
        {
            _pin?.Dispose();
            Process.Dispose();
        }
    }

    /// <summary>Takes ownership of an already-started process, disposing it if the wrap itself throws.</summary>
    /// <param name="logger">Receives the Warning when a kill cannot be confirmed complete.</param>
    public static LinuxProcessGroupHandle Wrap(Process process, ProcessStderrTail? stderrTail = null, ILogger? logger = null) =>
        Wrap(process, stderrTail, logger, LinuxPidFd.Open, static (pidFd, signal, group) => pidFd.Send(signal, group), LinuxProcFs.Signal,
            DefaultGrace, DefaultKillVerifyBound);

    /// <summary>Seam: the pidfd calls, <c>kill(2)</c> (0, else the errno) and both waits are swapped out by tests.</summary>
    /// <remarks>
    ///     The pin is kept only when the child is still unexited AFTER the open, so it cannot name a process that reused the pid. That proof
    ///     fails where the runtime may reap before <c>HasExited</c> flips (<paramref name="hostVerifiable" /> false): no pin, the numeric path.
    /// </remarks>
    internal static LinuxProcessGroupHandle Wrap(Process process,
        ProcessStderrTail? stderrTail,
        ILogger? logger,
        Func<int, LinuxPidFd> openPidFd,
        Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> sendPidFd,
        Func<int, int, int> signal,
        TimeSpan grace,
        TimeSpan killVerifyBound,
        Func<bool>? hostVerifiable = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        LinuxPidFd? pin = null;
        try
        {
            var processId = process.Id;
            pin = (hostVerifiable ?? LinuxProcessTreeKill.IsHostVerifiable)() ? openPidFd(processId) : LinuxPidFd.Unsupported();
            var pinStatus = pin.OpenStatus;
            if (pinStatus != LinuxPidFdOpenStatus.Opened || ProcessExitState.SafeHasExited(process))
            {
                pin.Dispose();
                pin = null;
                pinStatus = pinStatus == LinuxPidFdOpenStatus.Opened ? LinuxPidFdOpenStatus.Gone : pinStatus;
            }

            return new LinuxProcessGroupHandle(process, processId, stderrTail, logger ?? NullLogger.Instance, pin, pinStatus, sendPidFd, signal, grace,
                killVerifyBound);
        }
        catch
        {
            pin?.Dispose();
            process.Dispose();
            throw;
        }
    }

    /// <summary>Starts a process through <paramref name="start" /> and wraps it, so the caller never holds a bare process.</summary>
    public static LinuxProcessGroupHandle Start(Func<Process> start, ProcessStderrTail? stderrTail = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        return Wrap(start(), stderrTail, logger);
    }

    /// <summary>Group TERM, grace, group and leader KILL, all through the pin; returns why the kill is incomplete, or null.</summary>
    private string? TerminatePinned(LinuxPidFd pin)
    {
        switch (Send(pin, Sigterm, group: true))
        {
            case LinuxPidFdSendStatus.Failed:
                return "the group TERM through the pidfd failed, so nothing else was sent";
            case LinuxPidFdSendStatus.GroupFlagUnsupported:
                return TerminateWithoutGroupFlag(pin);
            case LinuxPidFdSendStatus.Gone:
                // No group under the leader's pid: it is empty, or the child has not reached setsid(2) yet and is still the leader alone.
                switch (Send(pin, 0, group: false))
                {
                    case LinuxPidFdSendStatus.Gone:
                        return null;
                    case LinuxPidFdSendStatus.Failed:
                        return "the leader probe through the pidfd failed";
                }

                if (Send(pin, Sigterm, group: false) == LinuxPidFdSendStatus.Failed)
                {
                    return "the leader TERM through the pidfd failed";
                }

                break;
        }

        // The grace is the whole group's: a member still shutting down after its leader exited gets the rest of it before any KILL.
        if (WithinGrace(() => Send(pin, 0, group: true) == LinuxPidFdSendStatus.Gone && Send(pin, 0, group: false) == LinuxPidFdSendStatus.Gone))
        {
            return null;
        }

        var groupKill = Send(pin, Sigkill, group: true);
        var leaderKill = Send(pin, Sigkill, group: false);
        if (groupKill == LinuxPidFdSendStatus.Failed || leaderKill == LinuxPidFdSendStatus.Failed)
        {
            return "the KILL through the pidfd failed";
        }

        // Both: before setsid(2) the group probe reads Gone while the leader alone still runs.
        if (!Process.WaitForExit(_killVerifyBound))
        {
            return "the leader outlived its KILL";
        }

        return WithinBound(() => Send(pin, 0, group: true) == LinuxPidFdSendStatus.Gone) ? null : "the group still had members after its KILL";
    }

    /// <summary>A kernel before 6.9: numeric group signals, each only while the pinned leader is alive (the documented residual).</summary>
    private string? TerminateWithoutGroupFlag(LinuxPidFd pin)
    {
        switch (Send(pin, 0, group: false))
        {
            case LinuxPidFdSendStatus.Failed:
                return "the leader probe through the pidfd failed, so nothing numeric was sent";
            case LinuxPidFdSendStatus.Gone:
                return GroupEmptyByNumber() ? null : "the group outlived its leader, and this kernel has no pidfd group signal";
        }

        var term = _signal(-_processGroupId, Sigterm);
        if (term == Esrch)
        {
            // No group yet: the child has not reached setsid(2), so the leader alone gets the TERM.
            _ = Send(pin, Sigterm, group: false);
        }
        else if (term != 0)
        {
            return "the numeric group TERM failed";
        }

        var graceStarted = Stopwatch.GetTimestamp();
        _ = Process.WaitForExit(_grace);
        switch (Send(pin, 0, group: false))
        {
            case LinuxPidFdSendStatus.Failed:
                return "the leader probe through the pidfd failed after the TERM";
            case LinuxPidFdSendStatus.Gone:
                // The leader died of the TERM: no KILL is allowed, but its members keep the rest of the grace before the probe below.
                _ = SpinWait.SpinUntil(() => _signal(-_processGroupId, 0) == Esrch, RemainingGrace(graceStarted));
                break;
            case LinuxPidFdSendStatus.Sent:
                // Still alive, so its pid still names its group: the one moment a numeric KILL is allowed.
                var kill = _signal(-_processGroupId, Sigkill);
                _ = Send(pin, Sigkill, group: false);
                if (kill is not (0 or Esrch))
                {
                    return "the numeric group KILL failed";
                }

                if (!Process.WaitForExit(_killVerifyBound))
                {
                    return "the leader outlived its KILL";
                }

                break;
        }

        // A leader that died after the TERM permits no KILL: only the probe below.
        return GroupEmptyByNumber() ? null : "the group outlived its leader, and this kernel has no pidfd group signal";
    }

    /// <summary>No usable pin: today's numeric path, sent only while this host's own child is unexited, so its pid still names its group.</summary>
    private string? TerminateUnpinned()
    {
        if (_pinStatus is (LinuxPidFdOpenStatus.Failed or LinuxPidFdOpenStatus.Unsupported) && !ProcessExitState.SafeHasExited(Process))
        {
            if (!SignalGroupOrLeader(Sigterm))
            {
                return "the numeric group TERM failed";
            }

            if (!Process.WaitForExit(_grace) && !ProcessExitState.SafeHasExited(Process))
            {
                if (!SignalGroupOrLeader(Sigkill))
                {
                    return "the numeric group KILL failed";
                }

                if (!Process.WaitForExit(_killVerifyBound))
                {
                    return "the leader outlived its KILL";
                }
            }
        }

        // Opened too late (the child had already exited) or exited since: its members are only probed, never signalled.
        return GroupEmptyByNumber() ? null : "the group outlived its leader, which could not be pinned";
    }

    /// <summary>Signals the group by number; a group not formed yet (the child has not reached setsid(2)) gets the unexited leader alone.</summary>
    /// <returns>False on an errno other than ESRCH.</returns>
    private bool SignalGroupOrLeader(int signal)
    {
        var errno = _signal(-_processGroupId, signal);
        if (errno == Esrch && !ProcessExitState.SafeHasExited(Process))
        {
            errno = _signal(_processGroupId, signal);
        }

        return errno is 0 or Esrch;
    }

    private LinuxPidFdSendStatus Send(LinuxPidFd pin, int signal, bool group) =>
        _sendPidFd(pin, signal, group).Status;

    /// <summary>Only ESRCH from the non-delivering <c>kill(-pgid, 0)</c> proves the group empty.</summary>
    private bool GroupEmptyByNumber() =>
        WithinBound(() => _signal(-_processGroupId, 0) == Esrch);

    /// <summary>Re-checks <paramref name="done" /> until it holds or the verify bound passes: a killed member stays in its group until reaped.</summary>
    private bool WithinBound(Func<bool> done) =>
        // Forced sync: TreeKill and Dispose are synchronous contracts; SpinWait backs off instead of blocking in Thread.Sleep.
        SpinWait.SpinUntil(done, _killVerifyBound);

    /// <summary>Waits for the leader, then re-checks <paramref name="done" /> for what is left of the grace; true once it holds.</summary>
    private bool WithinGrace(Func<bool> done)
    {
        var started = Stopwatch.GetTimestamp();
        _ = Process.WaitForExit(_grace);
        return SpinWait.SpinUntil(done, RemainingGrace(started));
    }

    private TimeSpan RemainingGrace(long started)
    {
        var remaining = _grace - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
