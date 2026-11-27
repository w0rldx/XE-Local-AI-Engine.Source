namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Reads trainer-process identity out of <c>/proc</c> and signals process groups.</summary>
/// <remarks>
///     The receipt-validation half of the <c>SandboxOrphanReaper</c> model: identity is proven from several
///     independent fields before anything is signaled, never from an executable-path match alone the way
///     the shared <c>StaleProcessReaper</c> does it.
/// </remarks>
internal sealed partial class LinuxTrainingProcessInspector : ITrainingProcessInspector
{
    /// <summary>The variable the run token travels to the child in, and is read back from, in <c>/proc/[pid]/environ</c>.</summary>
    public const string RunTokenVariable = "XE_TRAINING_RUN_TOKEN";

    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int Esrch = 3;

    /// <summary>The first read buffer for <c>/proc/[pid]/environ</c>; a fuller environment doubles it up to <see cref="MaxEnvironmentBytes" />.</summary>
    internal const int InitialEnvironmentBytes = 256 * 1024;

    /// <summary>Twice the default 2 MiB the kernel lets argv and envp take together (a quarter of an 8 MiB stack).</summary>
    internal const int MaxEnvironmentBytes = 4 * 1024 * 1024;

    internal static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the post-SIGKILL re-inspection waits for the pid to be provably gone.</summary>
    internal static readonly TimeSpan KillVerifyBound = TimeSpan.FromSeconds(10);

    private readonly Func<int, int, int> _kill;
    private readonly ILogger<LinuxTrainingProcessInspector> _logger;
    private readonly TrainerPidFdOps _pidFd;
    private readonly Func<int, TrainingProcessStatRead> _readStat;
    private readonly TimeProvider _timeProvider;

    public LinuxTrainingProcessInspector(TimeProvider timeProvider, ILogger<LinuxTrainingProcessInspector> logger)
        : this(timeProvider, logger, ReadStat, LinuxKill.Send, TrainerPidFdOps.Real)
    {
    }

    /// <summary>Seam for the identity check: the stat reader, <c>kill(2)</c> and the pidfd calls are swapped out by tests.</summary>
    /// <param name="pidFd">The pidfd calls; unsupported when omitted, so a test that scripts only <c>kill(2)</c> keeps the numeric path.</param>
    internal LinuxTrainingProcessInspector(TimeProvider timeProvider,
        ILogger<LinuxTrainingProcessInspector> logger,
        Func<int, TrainingProcessStatRead> readStat,
        Func<int, int, int> kill,
        TrainerPidFdOps? pidFd = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(readStat);
        ArgumentNullException.ThrowIfNull(kill);
        _timeProvider = timeProvider;
        _logger = logger;
        _readStat = readStat;
        _kill = kill;
        _pidFd = pidFd ?? TrainerPidFdOps.Unsupported;
    }

    public TrainingProcessFacts? Inspect(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsLinux())
        {
            return null;
        }

        if (ReadStatOrNull(processId) is not { } stat)
        {
            return null;
        }

        return new TrainingProcessFacts
        {
            Pgid = stat.Pgid,
            StartTicks = stat.StartTicks,
            ExecutablePath = ResolveExecutablePath(processId),
            RunToken = ReadRunToken(processId)
        };
    }

    public TrainingProcessPresence ProbePresence(int processId) =>
        processId <= 0 || !OperatingSystem.IsLinux() ? TrainingProcessPresence.Gone : _readStat(processId).Presence;

    /// <summary>This host's own process group, read live; 0 off Linux.</summary>
    public static int HostProcessGroupId => OperatingSystem.IsLinux() ? GetProcessGroup() : 0;

    int ITrainingProcessInspector.HostProcessGroupId => HostProcessGroupId;

    public bool ProcessGroupHasMembers(int processGroupId) =>
        OperatingSystem.IsLinux() && (processGroupId <= 1 || processGroupId == HostProcessGroupId || GroupHasMembers(_kill, processGroupId));

    /// <summary>Signal 0 checks without sending anything; only ESRCH proves the group empty, so a refusal (EPERM) still counts as a member.</summary>
    /// <param name="kill"><c>kill(2)</c>, leaving its errno in <see cref="Marshal.GetLastPInvokeError" />.</param>
    /// <param name="processGroupId">The group, not negated.</param>
    internal static bool GroupHasMembers(Func<int, int, int> kill, int processGroupId) =>
        kill(-processGroupId, 0) == 0 || Marshal.GetLastPInvokeError() != Esrch;

    public Task<TrainingProcessPresence> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default)
    {
        // 0 is the CALLER's group, -1 every process this user owns, and the host's own group is the node itself.
        if (processGroupId <= 1 || processGroupId == HostProcessGroupId || !OperatingSystem.IsLinux())
        {
            return Task.FromResult(TrainingProcessPresence.Unknown);
        }

        return TerminateAsync(-processGroupId, processGroupId, new ExpectedIdentity(expectedStartTicks, expectedRunToken), waitForGroup: true, cancellationToken);
    }

    public Task<TrainingProcessPresence> KillProcessAsync(int processId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default)
    {
        if (processId <= 1 || processId == Environment.ProcessId || !OperatingSystem.IsLinux())
        {
            return Task.FromResult(TrainingProcessPresence.Unknown);
        }

        // One probe, no wait: a trainer that ran setsid after its receipt was read leads a group named by its pid, which this never signals.
        return TerminateAsync(processId, processId, new ExpectedIdentity(expectedStartTicks, expectedRunToken), waitForGroup: false, cancellationToken);
    }

    /// <summary>
    ///     SIGTERM <paramref name="target" />, wait for <paramref name="identityPid" /> to vanish, SIGKILL, then re-inspect until it
    ///     is provably gone or <see cref="KillVerifyBound" /> passes. A signal is sent only while the pid is provably ours.
    /// </summary>
    /// <param name="waitForGroup">Whether a leader that vanished waits, within the same bound, for the group named by its pid to empty, or probes it once.</param>
    private async Task<TrainingProcessPresence> TerminateAsync(int target,
        int identityPid,
        ExpectedIdentity expected,
        bool waitForGroup,
        CancellationToken cancellationToken)
    {
        var expectedStartTicks = expected.StartTicks;
        // One verify bound for the whole kill, restarted only by the SIGKILL: a leader that dies late leaves its group what is left of it.
        var deadline = _timeProvider.GetUtcNow() + KillVerifyBound;
        var leader = SignalIfSameProcess(target, identityPid, expected, Sigterm);
        if (leader.Presence == TrainingProcessPresence.Alive)
        {
            leader = await WaitUntilGoneAsync(identityPid, expectedStartTicks, _timeProvider.GetUtcNow() + TerminateGrace, cancellationToken).ConfigureAwait(false);
            if (leader.Presence != TrainingProcessPresence.Gone)
            {
                leader = SignalIfSameProcess(target, identityPid, expected, Sigkill);
                if (leader.Presence == TrainingProcessPresence.Alive)
                {
                    deadline = _timeProvider.GetUtcNow() + KillVerifyBound;
                    leader = await WaitUntilGoneAsync(identityPid, expectedStartTicks, deadline, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // A changed start time proves the group empty too: a pid is not reallocated while it still names a group.
        if (leader.Presence != TrainingProcessPresence.Gone || leader.Recycled)
        {
            return leader.Presence;
        }

        return await GroupPresenceAsync(identityPid, waitForGroup ? deadline : _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gone once the group a vanished leader named is empty, polling until <paramref name="deadline" />; unknown while members are left.</summary>
    private async Task<TrainingProcessPresence> GroupPresenceAsync(int processGroupId, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        while (ProcessGroupHasMembers(processGroupId))
        {
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                _logger.LogWarning("The leader of process group {Pgid} is gone, but the group still has members; it is not reported gone.", processGroupId);
                return TrainingProcessPresence.Unknown;
            }

            await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return TrainingProcessPresence.Gone;
    }

    /// <summary>Polls until the pid is provably gone or <paramref name="deadline" /> passes; returns the last presence read and whether a changed start time proved it.</summary>
    private async Task<LeaderPresence> WaitUntilGoneAsync(int processId,
        long expectedStartTicks,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var leader = PresenceOf(_readStat(processId), expectedStartTicks);
            if (leader.Presence == TrainingProcessPresence.Gone || _timeProvider.GetUtcNow() >= deadline)
            {
                return leader;
            }

            await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Signals only a process proven to be the recorded one; otherwise returns what the reads prove: gone (recycled or vanished), unknown or deferred.</summary>
    /// <returns><see cref="TrainingProcessPresence.Alive" /> once the signal was sent.</returns>
    private LeaderPresence SignalIfSameProcess(int target, int identityPid, ExpectedIdentity expected, int signal)
    {
        if (SignalThroughPidFd(target, identityPid, expected, signal, _readStat, _pidFd, _kill, _logger) is { } pinned)
        {
            return pinned;
        }

        _logger.LogDebug("pidfd is unsupported here; pid {Pid} is verified by its stat alone before signal {Signal}.", identityPid, signal);
        var read = _readStat(identityPid);
        if (!MaySignal(read, expected.StartTicks))
        {
            var leader = PresenceOf(read, expected.StartTicks);
            _logger.LogWarning("Pid {Pid} is not provably the recorded process ({Presence}); signal {Signal} was not sent.", identityPid, leader.Presence, signal);
            return leader;
        }

        // Read-then-signal leaves a microsecond window: the residual where the kernel offers no pidfd.
        _ = _kill(target, signal);
        return LeaderPresence.Sent;
    }

    /// <summary>
    ///     Opens a pidfd on <paramref name="identityPid" />, re-verifies the recorded start time and run token after the open, then signals
    ///     <paramref name="target" /> (a negative one is the group the pid names) through the fd.
    /// </summary>
    /// <returns>Alive once sent; what the reads prove when the process is not the recorded one; deferred on an operational failure; null when pidfd is unsupported.</returns>
    /// <remarks>
    ///     The fd names whoever held the pid at the open, so a match read after it binds the fd to the recorded process, and a later reuse of the
    ///     pid only makes the send fail. An operational failure never falls back to a numeric signal.
    /// </remarks>
    internal static LeaderPresence? SignalThroughPidFd(int target,
        int identityPid,
        ExpectedIdentity expected,
        int signal,
        Func<int, TrainingProcessStatRead> readStat,
        TrainerPidFdOps ops,
        Func<int, int, int> kill,
        ILogger logger)
    {
        using var pidFd = ops.Open(identityPid);
        switch (pidFd.OpenStatus)
        {
            case LinuxPidFdOpenStatus.Unsupported:
                return null;
            case LinuxPidFdOpenStatus.Gone:
                return LeaderPresence.Vanished;
            case LinuxPidFdOpenStatus.Failed:
                logger.LogWarning("A pidfd on pid {Pid} could not be opened (errno {Errno}); signal {Signal} was not sent.", identityPid, pidFd.OpenErrno, signal);
                return LeaderPresence.Deferred;
        }

        var read = readStat(identityPid);
        if (read.Stat is null && read.Presence == TrainingProcessPresence.Unknown)
        {
            // A failed read after the open (EMFILE: the fd may have taken the last descriptor) learned nothing, so the receipt is kept.
            logger.LogWarning("Pid {Pid}'s stat could not be read after its pidfd was opened; signal {Signal} was not sent.", identityPid, signal);
            return LeaderPresence.Deferred;
        }

        if (!MaySignal(read, expected.StartTicks))
        {
            var leader = PresenceOf(read, expected.StartTicks);
            logger.LogWarning("Pid {Pid} is not provably the recorded process ({Presence}); signal {Signal} was not sent.", identityPid, leader.Presence, signal);
            return leader;
        }

        // Only a token read and found different, or absent from a non-empty environment, contradicts the receipt. A zombie's environment, or one
        // past exit_mm, went with its memory: there the start time and the open fd are the evidence, as a kill(-pgid) to its group always had.
        if (read.Presence == TrainingProcessPresence.Alive
            && ops.ReadRunToken(identityPid) is { } token
            && !string.Equals(token, expected.RunToken, StringComparison.Ordinal))
        {
            logger.LogWarning("Pid {Pid} does not provably carry the recorded run token; signal {Signal} was not sent.", identityPid, signal);
            return LeaderPresence.Deferred;
        }

        var sent = SendThroughPidFd(pidFd, target, signal, ops, kill);
        switch (sent.Status)
        {
            case LinuxPidFdSendStatus.Sent:
                return LeaderPresence.Sent;
            case LinuxPidFdSendStatus.Gone:
                // A group ESRCH means no member is left, not that the leader exited: only a fresh read says what the leader did.
                return target < 0 ? PresenceOf(readStat(identityPid), expected.StartTicks) : LeaderPresence.Vanished;
            default:
                logger.LogWarning("Signal {Signal} to pid {Pid} through its pidfd failed ({Status}, errno {Errno}); nothing was sent.", signal, identityPid, sent.Status, sent.Errno);
                return LeaderPresence.Deferred;
        }
    }

    /// <summary>Sends through <paramref name="pidFd" /> to the pid, or to the group it names when <paramref name="target" /> is negative.</summary>
    /// <remarks>
    ///     A group flag the kernel refuses (before 6.9, or filtered) gets a probe through the fd, then the numeric <c>kill(-pgid)</c>: the documented
    ///     residual, where the leader can die and its emptied group be reused between the two calls, which takes a full pid wrap. The outcome is
    ///     then the numeric kill's: ESRCH is an empty group, and any other errno a refusal, never the probe's success.
    /// </remarks>
    internal static LinuxPidFdSendResult SendThroughPidFd(LinuxPidFd pidFd, int target, int signal, TrainerPidFdOps ops, Func<int, int, int> kill)
    {
        var sent = ops.Send(pidFd, signal, target < 0);
        if (sent.Status != LinuxPidFdSendStatus.GroupFlagUnsupported)
        {
            return sent;
        }

        sent = ops.Send(pidFd, 0, false);
        if (sent.Status != LinuxPidFdSendStatus.Sent)
        {
            return sent;
        }

        if (kill(target, signal) == 0)
        {
            return new LinuxPidFdSendResult(LinuxPidFdSendStatus.Sent, Errno: 0);
        }

        var errno = Marshal.GetLastPInvokeError();
        return new LinuxPidFdSendResult(errno == Esrch ? LinuxPidFdSendStatus.Gone : LinuxPidFdSendStatus.Failed, errno);
    }

    /// <summary>True only when the stat was read now and carries the recorded start time: an unreadable pid may have been reused.</summary>
    internal static bool MaySignal(TrainingProcessStatRead read, long expectedStartTicks) =>
        read.Stat is { } stat && stat.StartTicks == expectedStartTicks;

    /// <summary>A changed start time reads gone and recycled; anything else is what the read says, a missing pid or a zombie gone but not recycled.</summary>
    private static LeaderPresence PresenceOf(TrainingProcessStatRead read, long expectedStartTicks) =>
        read.Stat is { } stat && stat.StartTicks != expectedStartTicks
            ? new LeaderPresence
            {
                Presence = TrainingProcessPresence.Gone,
                Recycled = true
            }
            : new LeaderPresence
            {
                Presence = read.Presence,
                Recycled = false
            };

    /// <summary>A leader's presence, and whether a changed start time proved it gone (recycled) rather than a missing pid or a zombie.</summary>
    internal sealed class LeaderPresence
    {
        public static readonly LeaderPresence Sent = new()
        {
            Presence = TrainingProcessPresence.Alive,
            Recycled = false
        };

        public static readonly LeaderPresence Vanished = new()
        {
            Presence = TrainingProcessPresence.Gone,
            Recycled = false
        };

        public static readonly LeaderPresence Deferred = new()
        {
            Presence = TrainingProcessPresence.Deferred,
            Recycled = false
        };

        public required TrainingProcessPresence Presence { get; init; }

        public required bool Recycled { get; init; }
    }

    /// <summary>What a receipt records about its process, which every signal re-verifies.</summary>
    internal readonly record struct ExpectedIdentity(long StartTicks, string RunToken);

    /// <summary>Process-group id (field 5) and start time (field 22) of <c>/proc/[pid]/stat</c>, or null when gone or unreadable.</summary>
    public static TrainingProcessStat? ReadStatOrNull(int processId) =>
        ReadStat(processId).Stat;

    internal static TrainingProcessStatRead ReadStat(int processId) =>
        ReadStatFile(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"));

    /// <summary>Reads one stat file: missing is gone, unreadable or unparsable is unknown, and a zombie is gone.</summary>
    internal static TrainingProcessStatRead ReadStatFile(string path)
    {
        var read = LinuxProcStat.Read(path);
        return read.Outcome switch
        {
            LinuxProcStatOutcome.Missing => TrainingProcessStatRead.Missing,
            LinuxProcStatOutcome.Unreadable => TrainingProcessStatRead.Unreadable,
            _ => new TrainingProcessStatRead
            {
                Presence = read.Exited ? TrainingProcessPresence.Gone : TrainingProcessPresence.Alive,
                Stat = new TrainingProcessStat(read.ProcessGroupId, read.StartTicks)
            }
        };
    }

    public static string? ResolveExecutablePath(int processId)
    {
        try
        {
            return File.ResolveLinkTarget(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/exe"), returnFinalTarget: true)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The run token in <c>/proc/[pid]/environ</c>: null when unreadable or empty (a process without memory: a zombie, or one past exit_mm), an empty string when absent from a non-empty one.</summary>
    /// <remarks>Which of the two a memoryless process gives depends on the kernel; neither contradicts a receipt.</remarks>
    internal static string? ReadRunToken(int processId) =>
        ReadEnvironmentBlock(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/environ"), InitialEnvironmentBytes, MaxEnvironmentBytes) is { } raw
            ? RunTokenIn(raw)
            : null;

    /// <summary>The whole file in ONE read call, re-opened with a doubled buffer while a read fills it; null when unreadable or larger than <paramref name="maxBytes" />.</summary>
    /// <remarks>
    ///     Several reads of one open <c>environ</c> span the target's execve: the open holds the old image's memory, which is gone by a later
    ///     read, so the block ends at a chunk boundary and its last variables, the run token among them, vanish. One read copies one image or nothing.
    /// </remarks>
    internal static byte[]? ReadEnvironmentBlock(string path, int initialBytes, int maxBytes)
    {
        for (var size = initialBytes; size <= maxBytes; size *= 2)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                int read;
                using (var handle = File.OpenHandle(path))
                {
                    // Forced sync: same synchronous ITrainingProcessSpawner.Inspect contract as ReadStatOrNull; procfs source.
#pragma warning disable MA0045 // Contract-forced: sync Inspect contract, procfs read.
                    read = RandomAccess.Read(handle, buffer.AsSpan(0, size), fileOffset: 0);
#pragma warning restore MA0045
                }

                if (read < size)
                {
                    return buffer[..read];
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return null;
    }

    /// <summary>The run token in a raw environment block: null when the block is empty, an empty string when the variable is absent from it.</summary>
    internal static string? RunTokenIn(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return null;
        }

        const string prefix = RunTokenVariable + "=";
        return Encoding.UTF8.GetString(raw)
                       .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                       .Where(static entry => entry.StartsWith(prefix, StringComparison.Ordinal))
                       .Select(static entry => entry[prefix.Length..])
                       .FirstOrDefault() ?? string.Empty;
    }

    // pid_t getpgrp(void); — the calling process's group, which a trainer must never be signaled through.
    [LibraryImport("libc", EntryPoint = "getpgrp")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetProcessGroup();
}
