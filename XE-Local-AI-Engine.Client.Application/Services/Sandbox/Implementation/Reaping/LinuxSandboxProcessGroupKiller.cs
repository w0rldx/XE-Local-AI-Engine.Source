namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using System.Globalization;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Linux <see cref="ISandboxProcessGroupKiller" />.</summary>
/// <remarks>
///     A pidfd pins the leader's pid, so a signal sent through it with the group flag (Linux 6.9) reaches the members even after the
///     leader died and was reaped, and never a process that reused the number. A numeric <c>kill(-pgid)</c> is sent only while the leader
///     is proven alive (a kernel without the flag, or without pidfd), the documented residual of <c>OsStaleProcessScanner</c>; once the
///     leader is gone only the non-delivering <c>kill(-pgid, 0)</c> probe is sent. Start times come from field 22 of <c>/proc/[pid]/stat</c>.
/// </remarks>
public sealed class LinuxSandboxProcessGroupKiller : ISandboxProcessGroupKiller
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Esrch = 3;

    // Grace between the polite and the forceful signal, matching LinuxProcessGroupHandle's 2s.
    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);

    // The owned probe runs under the registry lock when a sandbox is killed, so it stays short.
    private static readonly TimeSpan DefaultOwnedProbeBound = TimeSpan.FromMilliseconds(200);

    private readonly Func<int, LinuxPidFd> _openPidFd;
    private readonly TimeSpan _ownedProbeBound;
    private readonly Func<int, LinuxProcStat> _readStat;
    private readonly Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> _sendPidFd;
    private readonly Func<int, int, int> _signal;
    private readonly TimeProvider _timeProvider;

    /// <summary>The time source is injected: ActivatorUtilities injects the registered one for the reaper and the provider.</summary>
    public LinuxSandboxProcessGroupKiller(TimeProvider timeProvider)
        : this(timeProvider, LinuxPidFd.Open, static (pidFd, signal, group) => pidFd.Send(signal, group), LinuxKill.Send, ReadStat)
    {
    }

    /// <summary>Seam: the pidfd calls, <c>kill(2)</c> (0, else the errno) and the stat read are swapped out by tests.</summary>
    internal LinuxSandboxProcessGroupKiller(TimeProvider timeProvider,
        Func<int, LinuxPidFd> openPidFd,
        Func<LinuxPidFd, int, bool, LinuxPidFdSendResult> sendPidFd,
        Func<int, int, int> signal,
        Func<int, LinuxProcStat> readStat,
        TimeSpan? ownedProbeBound = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _ownedProbeBound = ownedProbeBound ?? DefaultOwnedProbeBound;
        _openPidFd = openPidFd;
        _sendPidFd = sendPidFd;
        _signal = signal;
        _readStat = readStat;
    }

    /// <inheritdoc />
    public long? GetProcessStartTicks(int processId)
    {
        var stat = _readStat(processId);
        return stat.Outcome == LinuxProcStatOutcome.Parsed ? stat.StartTicks : null;
    }

    /// <inheritdoc />
    public bool IsProcessAlive(int processId)
    {
        return processId > 0 && Directory.Exists(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}"));
    }

    /// <inheritdoc />
    public LinuxPidFd PinProcess(int processId) =>
        _openPidFd(processId);

    /// <inheritdoc />
    public async Task<SandboxGroupKillOutcome> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default)
    {
        if (processGroupId <= 1)
        {
            // 0 would signal the CALLER's group and 1 is init: never a sandbox group, whatever a corrupt marker says.
            return SandboxGroupKillOutcome.Mismatch;
        }

        using var pin = _openPidFd(processGroupId);
        switch (pin.OpenStatus)
        {
            case LinuxPidFdOpenStatus.Gone:
                // The LEADER is gone; its members may not be.
                return ProbeGroup(processGroupId);
            case LinuxPidFdOpenStatus.Failed:
                return SandboxGroupKillOutcome.Deferred;
            case LinuxPidFdOpenStatus.Unsupported:
                return await KillByStatAsync(processGroupId, expectedStartTicks, cancellationToken);
        }

        // Read after the open, so a match binds the fd to the recorded leader and a later pid reuse only makes a send fail.
        var stat = _readStat(processGroupId);
        if (stat.Outcome != LinuxProcStatOutcome.Parsed)
        {
            return _sendPidFd(pin, 0, false).Status == LinuxPidFdSendStatus.Gone ? ProbeGroup(processGroupId) : SandboxGroupKillOutcome.Deferred;
        }

        if (stat.StartTicks != expectedStartTicks)
        {
            return SandboxGroupKillOutcome.Mismatch;
        }

        if (stat.ProcessGroupId != processGroupId)
        {
            // The recorded leader, not (or no longer) leading the group its pid names: only the leader itself is ours to signal.
            return Delivered(_sendPidFd(pin, Sigkill, false).Status);
        }

        // A zombie leader with the recorded start time still pins the group, so its members are reached through it.
        var term = _sendPidFd(pin, Sigterm, true).Status;
        switch (term)
        {
            case LinuxPidFdSendStatus.Gone:
                return SandboxGroupKillOutcome.Gone;
            case LinuxPidFdSendStatus.Failed:
                return SandboxGroupKillOutcome.Deferred;
            case LinuxPidFdSendStatus.GroupFlagUnsupported:
                return await KillByNumberAsync(processGroupId, () => LeaderAlive(pin), cancellationToken);
        }

        if (await WaitUntilAsync(() => _sendPidFd(pin, 0, true).Status == LinuxPidFdSendStatus.Gone, cancellationToken))
        {
            return SandboxGroupKillOutcome.Killed;
        }

        return _sendPidFd(pin, Sigkill, true).Status == LinuxPidFdSendStatus.Failed ? SandboxGroupKillOutcome.Deferred : SandboxGroupKillOutcome.Killed;
    }

    /// <inheritdoc />
    public SandboxGroupKillOutcome KillOwnedProcessGroup(LinuxPidFd? leaderPin,
        LinuxPidFdOpenStatus pinStatus,
        int processGroupId,
        Func<bool> leaderExited,
        Func<bool> killTree)
    {
        ArgumentNullException.ThrowIfNull(leaderExited);
        ArgumentNullException.ThrowIfNull(killTree);
        if (processGroupId <= 1)
        {
            _ = killTree();
            return SandboxGroupKillOutcome.Mismatch;
        }

        if (leaderPin is not null && _sendPidFd(leaderPin, 0, true).Status != LinuxPidFdSendStatus.GroupFlagUnsupported)
        {
            // The tree walk first, while the leader still parents its descendants; the pinned group kill works after its death.
            var walked = killTree();
            return WithTree(walked, Delivered(_sendPidFd(leaderPin, Sigkill, true).Status));
        }

        // No group flag, or no pin: the tree walk still goes first, since a numeric group KILL would take the leader and orphan a descendant
        // outside the group before the walk could find it. The numeric KILL follows only while the leader is unreaped (a zombie still holds its pid).
        var treeComplete = killTree();
        var leaderUnreaped = leaderPin is not null
            ? _sendPidFd(leaderPin, 0, false).Status == LinuxPidFdSendStatus.Sent
            : pinStatus is (LinuxPidFdOpenStatus.Failed or LinuxPidFdOpenStatus.Unsupported) && !leaderExited();
        return WithTree(treeComplete, leaderUnreaped ? ByErrno(_signal(-processGroupId, Sigkill)) : ProbeOwnedGroup(processGroupId));
    }

    /// <summary>An unfinished tree walk may have missed a descendant that left the group, which no group outcome can vouch for.</summary>
    private static SandboxGroupKillOutcome WithTree(bool treeComplete, SandboxGroupKillOutcome group) =>
        treeComplete ? group : SandboxGroupKillOutcome.Deferred;

    /// <summary>The leader is reaped, so its members are only probed, never signalled: ESRCH within the bound, as freshly killed members are reaped.</summary>
    private SandboxGroupKillOutcome ProbeOwnedGroup(int processGroupId) =>
        SpinWait.SpinUntil(() => _signal(-processGroupId, 0) == Esrch, _ownedProbeBound) ? SandboxGroupKillOutcome.Gone : SandboxGroupKillOutcome.Deferred;

    private static SandboxGroupKillOutcome Delivered(LinuxPidFdSendStatus status) =>
        status switch
        {
            LinuxPidFdSendStatus.Sent => SandboxGroupKillOutcome.Killed,
            LinuxPidFdSendStatus.Gone => SandboxGroupKillOutcome.Gone,
            _ => SandboxGroupKillOutcome.Deferred
        };

    private static SandboxGroupKillOutcome ByErrno(int errno) =>
        errno switch
        {
            0 => SandboxGroupKillOutcome.Killed,
            Esrch => SandboxGroupKillOutcome.Gone,
            _ => SandboxGroupKillOutcome.Deferred
        };

    private bool? LeaderAlive(LinuxPidFd pin) =>
        _sendPidFd(pin, 0, false).Status switch
        {
            LinuxPidFdSendStatus.Sent => true,
            LinuxPidFdSendStatus.Gone => false,
            _ => null
        };

    /// <summary>Only ESRCH from the non-delivering <c>kill(-pgid, 0)</c> proves the group empty; members or a refusal keep it.</summary>
    private SandboxGroupKillOutcome ProbeGroup(int processGroupId) =>
        _signal(-processGroupId, 0) == Esrch ? SandboxGroupKillOutcome.Gone : SandboxGroupKillOutcome.Deferred;

    /// <summary>The numeric path where the kernel offers no pidfd: the leader is identified by its stat before every signal.</summary>
    private Task<SandboxGroupKillOutcome> KillByStatAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken)
    {
        var stat = _readStat(processGroupId);
        return stat.Outcome switch
        {
            LinuxProcStatOutcome.Missing => Task.FromResult(ProbeGroup(processGroupId)),
            LinuxProcStatOutcome.Unreadable => Task.FromResult(SandboxGroupKillOutcome.Deferred),
            _ when stat.StartTicks != expectedStartTicks || stat.ProcessGroupId != processGroupId => Task.FromResult(SandboxGroupKillOutcome.Mismatch),
            _ => KillByNumberAsync(processGroupId, () => LeaderAliveByStat(processGroupId, expectedStartTicks), cancellationToken)
        };
    }

    private bool? LeaderAliveByStat(int processId, long expectedStartTicks)
    {
        var stat = _readStat(processId);
        return stat.Outcome switch
        {
            LinuxProcStatOutcome.Missing => false,
            LinuxProcStatOutcome.Unreadable => null,
            _ => stat.StartTicks == expectedStartTicks
        };
    }

    /// <summary>Numeric <c>kill(-pgid)</c> TERM, a grace, then KILL, each only while <paramref name="leaderAlive" /> proves the leader alive.</summary>
    /// <remarks>A leader that died in the grace leaves its group to the probe: no KILL is sent after the leader's death.</remarks>
    private async Task<SandboxGroupKillOutcome> KillByNumberAsync(int processGroupId, Func<bool?> leaderAlive, CancellationToken cancellationToken)
    {
        switch (leaderAlive())
        {
            case null:
                return SandboxGroupKillOutcome.Deferred;
            case false:
                return ProbeGroup(processGroupId);
        }

        var term = ByErrno(_signal(-processGroupId, Sigterm));
        if (term != SandboxGroupKillOutcome.Killed)
        {
            return term;
        }

        _ = await WaitUntilAsync(() => leaderAlive() != true, cancellationToken);
        var outcome = leaderAlive() switch
        {
            true => ByErrno(_signal(-processGroupId, Sigkill)),
            false => ProbeGroup(processGroupId),
            null => SandboxGroupKillOutcome.Deferred
        };
        return outcome == SandboxGroupKillOutcome.Gone ? SandboxGroupKillOutcome.Killed : outcome;
    }

    /// <summary>Polls <paramref name="done" /> until it holds or the grace passes, rather than waiting out the whole grace.</summary>
    private async Task<bool> WaitUntilAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + TerminateGrace;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            if (done())
            {
                return true;
            }

            await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken);
        }

        return done();
    }

    private static LinuxProcStat ReadStat(int processId) =>
        processId > 0 ? LinuxProcStat.Read(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat")) : LinuxProcStat.Missing;
}
