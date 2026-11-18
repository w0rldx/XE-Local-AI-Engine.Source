namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
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

    internal static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How long the post-SIGKILL re-inspection waits for the pid to be provably gone.</summary>
    internal static readonly TimeSpan KillVerifyBound = TimeSpan.FromSeconds(10);

    private readonly Func<int, int, int> _kill;
    private readonly ILogger<LinuxTrainingProcessInspector> _logger;
    private readonly Func<int, TrainingProcessStatRead> _readStat;
    private readonly TimeProvider _timeProvider;

    public LinuxTrainingProcessInspector(TimeProvider timeProvider, ILogger<LinuxTrainingProcessInspector> logger)
        : this(timeProvider, logger, ReadStat, Kill)
    {
    }

    /// <summary>Seam for the identity check: the stat reader and <c>kill(2)</c> are swapped out by tests.</summary>
    internal LinuxTrainingProcessInspector(TimeProvider timeProvider,
        ILogger<LinuxTrainingProcessInspector> logger,
        Func<int, TrainingProcessStatRead> readStat,
        Func<int, int, int> kill)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(readStat);
        ArgumentNullException.ThrowIfNull(kill);
        _timeProvider = timeProvider;
        _logger = logger;
        _readStat = readStat;
        _kill = kill;
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

    public Task<TrainingProcessPresence> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default)
    {
        // 0 is the CALLER's group, -1 every process this user owns, and the host's own group is the node itself.
        if (processGroupId <= 1 || processGroupId == HostProcessGroupId || !OperatingSystem.IsLinux())
        {
            return Task.FromResult(TrainingProcessPresence.Unknown);
        }

        return TerminateAsync(-processGroupId, processGroupId, expectedStartTicks, waitForGroup: true, cancellationToken);
    }

    public Task<TrainingProcessPresence> KillProcessAsync(int processId, long expectedStartTicks, CancellationToken cancellationToken = default)
    {
        if (processId <= 1 || processId == Environment.ProcessId || !OperatingSystem.IsLinux())
        {
            return Task.FromResult(TrainingProcessPresence.Unknown);
        }

        // One probe, no wait: a trainer that ran setsid after its receipt was read leads a group named by its pid, which this never signals.
        return TerminateAsync(processId, processId, expectedStartTicks, waitForGroup: false, cancellationToken);
    }

    /// <summary>
    ///     SIGTERM <paramref name="target" />, wait for <paramref name="identityPid" /> to vanish, SIGKILL, then re-inspect until it
    ///     is provably gone or <see cref="KillVerifyBound" /> passes. A signal is sent only while the pid is provably ours.
    /// </summary>
    /// <param name="waitForGroup">Whether a leader that vanished waits, within the same bound, for the group named by its pid to empty, or probes it once.</param>
    private async Task<TrainingProcessPresence> TerminateAsync(int target,
        int identityPid,
        long expectedStartTicks,
        bool waitForGroup,
        CancellationToken cancellationToken)
    {
        // One verify bound for the whole kill, restarted only by the SIGKILL: a leader that dies late leaves its group what is left of it.
        var deadline = _timeProvider.GetUtcNow() + KillVerifyBound;
        var leader = SignalIfSameProcess(target, identityPid, expectedStartTicks, Sigterm);
        if (leader.Presence == TrainingProcessPresence.Alive)
        {
            leader = await WaitUntilGoneAsync(identityPid, expectedStartTicks, _timeProvider.GetUtcNow() + TerminateGrace, cancellationToken).ConfigureAwait(false);
            if (leader.Presence != TrainingProcessPresence.Gone)
            {
                leader = SignalIfSameProcess(target, identityPid, expectedStartTicks, Sigkill);
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

    /// <summary>Signals only on a fresh stat read that matches; otherwise returns what the read proves: gone (recycled or vanished), or unknown.</summary>
    /// <returns><see cref="TrainingProcessPresence.Alive" /> once the signal was sent.</returns>
    private LeaderPresence SignalIfSameProcess(int target, int identityPid, long expectedStartTicks, int signal)
    {
        var read = _readStat(identityPid);
        if (!MaySignal(read, expectedStartTicks))
        {
            var leader = PresenceOf(read, expectedStartTicks);
            _logger.LogWarning("Pid {Pid} is not provably the recorded process ({Presence}); signal {Signal} was not sent.", identityPid, leader.Presence, signal);
            return leader;
        }

        // Read-then-signal still leaves a microsecond window; pidfd_open + pidfd_send_signal is the upgrade that closes it.
        _ = _kill(target, signal);
        return new LeaderPresence
        {
            Presence = TrainingProcessPresence.Alive,
            Recycled = false
        };
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
    private sealed class LeaderPresence
    {
        public required TrainingProcessPresence Presence { get; init; }

        public required bool Recycled { get; init; }
    }

    /// <summary>Process-group id (field 5) and start time (field 22) of <c>/proc/[pid]/stat</c>, or null when gone or unreadable.</summary>
    public static TrainingProcessStat? ReadStatOrNull(int processId) =>
        ReadStat(processId).Stat;

    internal static TrainingProcessStatRead ReadStat(int processId) =>
        ReadStatFile(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"));

    /// <summary>Reads one stat file: missing is gone, unreadable or unparsable is unknown, and a zombie is gone.</summary>
    internal static TrainingProcessStatRead ReadStatFile(string path)
    {
        string raw;
        try
        {
            // Forced sync: reached from the synchronous ITrainingProcessSpawner.Inspect contract and from the spawner's
            // post-start identity read; the source is procfs, which never blocks on a device.
#pragma warning disable MA0045 // Contract-forced: sync Inspect contract, procfs read.
            raw = File.ReadAllText(path);
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return TrainingProcessStatRead.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return TrainingProcessStatRead.Unreadable;
        }

        // Field 2 (comm) is parenthesized and may contain spaces and parentheses, so the rest is located from the LAST
        // ')'. Tokens then start at field 3 (state), putting pgrp at index 2 and starttime at index 19.
        var commEnd = raw.LastIndexOf(')');
        if (commEnd < 0 || commEnd + 2 >= raw.Length)
        {
            return TrainingProcessStatRead.Unreadable;
        }

        var fields = raw[(commEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int stateIndex = 0;
        const int pgidIndex = 2;
        const int startTimeIndex = 19;
        if (fields.Length <= startTimeIndex
            || !int.TryParse(fields[pgidIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgid)
            || !long.TryParse(fields[startTimeIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks))
        {
            return TrainingProcessStatRead.Unreadable;
        }

        // Z (zombie) and X (dead) hold no memory and run nothing; only the parent's wait() is left.
        var exited = fields[stateIndex] is "Z" or "X";
        return new TrainingProcessStatRead
        {
            Presence = exited ? TrainingProcessPresence.Gone : TrainingProcessPresence.Alive,
            Stat = new TrainingProcessStat(pgid, startTicks)
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

    private static string? ReadRunToken(int processId)
    {
        byte[] raw;
        try
        {
            // Forced sync: same synchronous ITrainingProcessSpawner.Inspect contract as ReadStatOrNull; procfs source.
#pragma warning disable MA0045 // Contract-forced: sync Inspect contract, procfs read.
            raw = File.ReadAllBytes(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/environ"));
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
