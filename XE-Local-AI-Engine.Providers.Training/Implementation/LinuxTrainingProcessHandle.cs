namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Why a trainer handle holds the pidfd it holds, which decides how every signal is verified.</summary>
internal enum TrainerPidFdState
{
    /// <summary>pidfd is unsupported here: the start-time check, then the numeric <c>kill(2)</c>, the only path that may signal a number.</summary>
    Legacy,

    /// <summary>An fd opened right after the start and proven to name this child: every signal goes through it.</summary>
    Pinned,

    /// <summary>The open at spawn failed operationally: each signal opens an fd and re-verifies start time and run token through it.</summary>
    Unpinned,

    /// <summary>The spawn could not prove the pid is this child (it exited, or carries another run token): nothing is ever signalled.</summary>
    Refused
}

/// <summary>What a poll of a pinned trainer's group through its fd learned by the deadline.</summary>
internal enum GroupWaitResult
{
    /// <summary>The group probe read gone: no member is left.</summary>
    Empty,

    /// <summary>A member still answered the last probe, taken at the deadline.</summary>
    PresentAtDeadline,

    /// <summary>The probe failed or the kernel refused the group flag, so nothing is known and nothing is killed.</summary>
    ProbeUnavailable
}

/// <summary>
///     A running trainer. Signals target the process GROUP, so the dataloader workers and compile subprocesses the
///     trainer forked die with it.
/// </summary>
/// <remarks>
///     <see cref="RequestStop" /> stops at SIGTERM because <c>train.py</c> handles it cooperatively, and a SIGKILL
///     there would turn every operator cancel into a failure.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxTrainingProcessHandle : ITrainingProcessHandle
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    // KillGroup is synchronous (Dispose runs it), and the leader's exit wait returns at once after the leader is gone, so only a sleep paces a group poll.
#pragma warning disable RS0030
    private static readonly Action<TimeSpan> BlockingSleep = Thread.Sleep;
#pragma warning restore RS0030
    private readonly Func<int, int, int> _kill;
    private readonly ILogger _logger;
    private readonly Process _process;
    private readonly Channel<string> _output;
    private readonly LinuxPidFd? _pidFd;
    private readonly TrainerPidFdOps _pidFdOps;
    private readonly TrainerPidFdState _pidFdState;
    private readonly SemaphoreSlim _stdinGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, GroupWaitResult> _waitForGroupEmpty;
    private readonly Func<TimeSpan, bool> _waitForExit;

    private int _disposed;

    public LinuxTrainingProcessHandle(Process process,
        TrainingLaunchReceipt receipt,
        Channel<string> output,
        ILogger logger,
        TimeProvider timeProvider)
        : this(process, receipt, output, logger, timeProvider, LinuxKill.Send, process.WaitForExit)
    {
    }

    /// <summary>The spawner's handle, which owns <paramref name="pin" />'s fd and signals through the real pidfd calls.</summary>
    internal LinuxTrainingProcessHandle(Process process,
        TrainingLaunchReceipt receipt,
        Channel<string> output,
        ILogger logger,
        TimeProvider timeProvider,
        LinuxTrainingProcessSpawner.TrainerPin pin)
        : this(process, receipt, output, logger, timeProvider, LinuxKill.Send, process.WaitForExit, pin, TrainerPidFdOps.Real)
    {
    }

    /// <param name="kill"><c>kill(2)</c>.</param>
    /// <param name="waitForExit">The wait between the SIGTERM and the SIGKILL in <see cref="KillGroup" />; true once the trainer exited.</param>
    /// <param name="pin">What the spawn proved, and the fd the handle then owns; omitted, the numeric path.</param>
    /// <param name="pidFdOps">The pidfd calls behind <paramref name="pin" />; unsupported when omitted.</param>
    /// <param name="waitForGroupEmpty">The wait for a pinned group's survivors to leave after the SIGTERM; <see cref="PollGroupUntilEmpty" /> when omitted.</param>
    internal LinuxTrainingProcessHandle(Process process,
        TrainingLaunchReceipt receipt,
        Channel<string> output,
        ILogger logger,
        TimeProvider timeProvider,
        Func<int, int, int> kill,
        Func<TimeSpan, bool> waitForExit,
        LinuxTrainingProcessSpawner.TrainerPin pin = default,
        TrainerPidFdOps? pidFdOps = null,
        Func<TimeSpan, GroupWaitResult>? waitForGroupEmpty = null)
    {
        _logger = logger;
        _process = process;
        Receipt = receipt;
        _output = output;
        _timeProvider = timeProvider;
        _kill = kill;
        _waitForExit = waitForExit;
        _pidFdState = pin.State;
        _pidFd = pin.PidFd;
        _pidFdOps = pidFdOps ?? TrainerPidFdOps.Unsupported;
        _waitForGroupEmpty = waitForGroupEmpty ?? (timeout => PollGroupUntilEmpty(_pidFd!, _pidFdOps, timeProvider, BlockingSleep, timeout, logger));
    }

    public TrainingLaunchReceipt Receipt { get; }

    public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken) =>
        _output.Reader.ReadAllAsync(cancellationToken);

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async Task<int?> WaitForGroupExitAsync(CancellationToken cancellationToken)
    {
        // A pgid is not reused while any member lives, and a reuse of an empty one only reads as non-empty: both fail closed.
        var hostGroup = LinuxTrainingProcessInspector.HostProcessGroupId;
        try
        {
            if (!TrainingProcessGroupGuard.MaySignalGroup(Receipt.Pid, Receipt.Pgid, hostGroup))
            {
                // No group to probe: the pipe EOF is the only sign the trainer's children are gone, so keep waiting for it.
                var exitCode = await WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                // A receipt read before setsid ran holds the host's group, yet the trainer may lead one named by its pid; a child that closed its pipes stays there.
                while (Receipt.Pid > 1 && Receipt.Pid != hostGroup && GroupHasMembers(Receipt.Pid))
                {
                    await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                return exitCode;
            }

            // HasExited reads the wait status only; Process.WaitForExitAsync would also wait for pipes a descendant may hold open.
            while (!HasExited() || GroupHasMembers(Receipt.Pgid))
            {
                await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1; // Disposed: HasExited already reads it as exited, and its code is gone.
        }
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.AsSpan().ContainsAny('\n', '\r'))
        {
            throw new ArgumentException("A stdin command is one line; an embedded line break would split it into two.", nameof(line));
        }

        if (!_process.StartInfo.RedirectStandardInput)
        {
            throw new InvalidOperationException("This trainer was spawned without a stdin pipe.");
        }

        // The raw stream, not StandardInput's writer, so the encoding is UTF-8 without a BOM whatever the platform default.
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _stdinGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stdin = _process.StandardInput.BaseStream;
            await stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _stdinGate.Release();
        }
    }

    public void RequestStop()
    {
        if (HasExited())
        {
            return;
        }

        SignalIfSameProcess(SignalTarget(), Sigterm);
    }

    public void KillGroup()
    {
        if (HasExited())
        {
            KillSurvivingGroup();
            return;
        }

        var target = SignalTarget();
        var pinnedGroup = _pidFdState == TrainerPidFdState.Pinned && target < 0;
        var start = _timeProvider.GetTimestamp();
        SignalIfSameProcess(target, Sigterm);
        // A pinned group runs one grace from the SIGTERM, so a slow send does not stretch the leader's wait.
        if (!_waitForExit(pinnedGroup ? RemainingGrace(start) : KillGrace))
        {
            SignalIfSameProcess(target, Sigkill);
            return;
        }

        // The leader left within the grace; members that ignored the SIGTERM are reached only through the pinned fd.
        if (pinnedGroup)
        {
            KillGroupIfPresentAtDeadline(start);
        }
    }

    /// <summary>
    ///     Polls the group the pinned fd names until it reads gone, the probe stops answering, or <paramref name="timeout" /> passes;
    ///     the last sleep is capped to the deadline, and a zero timeout is one probe.
    /// </summary>
    internal static GroupWaitResult PollGroupUntilEmpty(LinuxPidFd pidFd,
        TrainerPidFdOps ops,
        TimeProvider timeProvider,
        Action<TimeSpan> sleep,
        TimeSpan timeout,
        ILogger logger)
    {
        var start = timeProvider.GetTimestamp();
        while (true)
        {
            var probe = ops.Send(pidFd, 0, true);
            if (probe.Status == LinuxPidFdSendStatus.Gone)
            {
                return GroupWaitResult.Empty;
            }

            if (probe.Status != LinuxPidFdSendStatus.Sent)
            {
                logger.LogWarning("The trainer group probe through its pidfd failed ({Status}, errno {Errno}); its survivors get no SIGKILL.", probe.Status, probe.Errno);
                return GroupWaitResult.ProbeUnavailable;
            }

            var remaining = timeout - timeProvider.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return GroupWaitResult.PresentAtDeadline;
            }

            sleep(remaining < ExitPollInterval ? remaining : ExitPollInterval);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        try
        {
            // Runs before the fd closes; a concurrent KillGroup then reads Gone from the closed fd and stops.
            KillGroup();
        }
        finally
        {
            _ = _output.Writer.TryComplete();
            _pidFd?.Dispose();
            _process.Dispose();
            _stdinGate.Dispose();
        }
    }

    /// <summary>
    ///     The leader has exited, but members it left in its group may ignore SIGTERM. Only a pinned fd still names that group, so only it
    ///     is signalled, and never the number: an emptied group's pgid may be reused.
    /// </summary>
    private void KillSurvivingGroup()
    {
        // The state first, and the guard without SignalTarget's warning, which would otherwise log on every Dispose.
        if (_pidFdState != TrainerPidFdState.Pinned
            || !TrainingProcessGroupGuard.MaySignalGroup(Receipt.Pid, Receipt.Pgid, LinuxTrainingProcessInspector.HostProcessGroupId))
        {
            return;
        }

        var probe = _pidFdOps.Send(_pidFd!, 0, true);
        if (probe.Status != LinuxPidFdSendStatus.Sent)
        {
            _logger.LogDebug("Trainer group {Pgid} left by exited pid {Pid} probes {Status} (errno {Errno}); nothing is signalled.", Receipt.Pgid, Receipt.Pid, probe.Status, probe.Errno);
            return;
        }

        var start = _timeProvider.GetTimestamp();
        var term = _pidFdOps.Send(_pidFd!, Sigterm, true);
        if (term.Status != LinuxPidFdSendStatus.Sent)
        {
            _logger.LogWarning("SIGTERM to trainer group {Pgid} through its pidfd read {Status} (errno {Errno}); no SIGKILL follows.", Receipt.Pgid, term.Status, term.Errno);
            return;
        }

        KillGroupIfPresentAtDeadline(start);
    }

    /// <summary>Waits out what is left of the grace that started at <paramref name="start" />, and sends SIGKILL through the fd only to a group still present then.</summary>
    private void KillGroupIfPresentAtDeadline(long start)
    {
        if (_waitForGroupEmpty(RemainingGrace(start)) != GroupWaitResult.PresentAtDeadline)
        {
            return;
        }

        var kill = _pidFdOps.Send(_pidFd!, Sigkill, true);
        _logger.LogInformation("Trainer group {Pgid} outlived the SIGTERM grace; SIGKILL through its pidfd read {Status} (errno {Errno}).", Receipt.Pgid, kill.Status, kill.Errno);
    }

    /// <summary>What is left of the SIGTERM grace that started at <paramref name="start" />, never below zero.</summary>
    private TimeSpan RemainingGrace(long start)
    {
        var remaining = KillGrace - _timeProvider.GetElapsedTime(start);
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    /// <summary>The negated group when the receipt proves the trainer leads a group of its own, else the bare pid.</summary>
    private int SignalTarget()
    {
        if (TrainingProcessGroupGuard.MaySignalGroup(Receipt.Pid, Receipt.Pgid, LinuxTrainingProcessInspector.HostProcessGroupId))
        {
            return -Receipt.Pgid;
        }

        _logger.LogWarning("Refusing to signal process group {Pgid} for trainer pid {Pid}; signalling the pid only.", Receipt.Pgid, Receipt.Pid);
        return Receipt.Pid;
    }

    /// <summary>Sends <paramref name="signal" /> only to a trainer proven to be this run's process, by the fd the spawn pinned or the checks its state allows.</summary>
    private void SignalIfSameProcess(int target, int signal)
    {
        switch (_pidFdState)
        {
            case TrainerPidFdState.Pinned:
                var sent = LinuxTrainingProcessInspector.SendThroughPidFd(_pidFd!, target, signal, _pidFdOps, _kill);
                if (sent.Status == LinuxPidFdSendStatus.Failed)
                {
                    _logger.LogWarning("Signal {Signal} to trainer pid {Pid} through its pidfd failed (errno {Errno}); nothing was sent.", signal, Receipt.Pid, sent.Errno);
                }

                return;
            case TrainerPidFdState.Unpinned:
                // No fd from the spawn: each signal opens one and re-verifies the receipt through it, as for a process this host does not own.
                if (LinuxTrainingProcessInspector.SignalThroughPidFd(target,
                        Receipt.Pid,
                        new LinuxTrainingProcessInspector.ExpectedIdentity(Receipt.StartTicks, Receipt.RunToken),
                        signal,
                        LinuxTrainingProcessInspector.ReadStat,
                        _pidFdOps,
                        _kill,
                        _logger) is null)
                {
                    _logger.LogWarning("Trainer pid {Pid} could not be pinned and pidfd is unavailable; signal {Signal} was not sent.", Receipt.Pid, signal);
                }

                return;
            case TrainerPidFdState.Refused:
                _logger.LogError("Trainer pid {Pid} was not proven to be this run's process at spawn; signal {Signal} was not sent.", Receipt.Pid, signal);
                return;
        }

        if (Receipt.StartTicks <= 0)
        {
            // The runtime reaps on SIGCHLD, so a pid it saw exit may name a stranger. Check-then-kill leaves the window Process.Kill
            // has: the residual where the kernel offers no pidfd.
            if (!HasExited())
            {
                _ = _kill(Receipt.Pid, signal);
                _logger.LogWarning("Trainer pid {Pid} has no recorded start time; signal {Signal} was sent to the pid only, unverified.", Receipt.Pid, signal);
            }

            return;
        }

        if (!LinuxTrainingProcessInspector.MaySignal(LinuxTrainingProcessInspector.ReadStat(Receipt.Pid), Receipt.StartTicks))
        {
            _logger.LogWarning("Trainer pid {Pid} no longer carries its recorded start time; signal {Signal} was not sent.", Receipt.Pid, signal);
            return;
        }

        _ = _kill(target, signal);
    }

    private bool GroupHasMembers(int processGroupId) =>
        LinuxTrainingProcessInspector.GroupHasMembers(_kill, processGroupId);

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true; // No associated process — treat as exited.
        }
    }
}
