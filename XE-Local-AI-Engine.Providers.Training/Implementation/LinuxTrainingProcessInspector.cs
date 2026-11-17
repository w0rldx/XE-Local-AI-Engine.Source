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

    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(5);
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

    public Task<TrainingProcessPresence> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default)
    {
        // 0 is the CALLER's group, -1 every process this user owns, and the host's own group is the node itself.
        if (processGroupId <= 1 || processGroupId == HostProcessGroupId || !OperatingSystem.IsLinux())
        {
            return Task.FromResult(TrainingProcessPresence.Unknown);
        }

        return TerminateAsync(-processGroupId, processGroupId, expectedStartTicks, cancellationToken);
    }

    public Task<TrainingProcessPresence> KillProcessAsync(int processId, long expectedStartTicks, CancellationToken cancellationToken = default)
    {
        if (processId <= 1 || processId == Environment.ProcessId || !OperatingSystem.IsLinux())
        {
            return Task.FromResult(TrainingProcessPresence.Unknown);
        }

        return TerminateAsync(processId, processId, expectedStartTicks, cancellationToken);
    }

    /// <summary>
    ///     SIGTERM <paramref name="target" />, wait for <paramref name="identityPid" /> to vanish, SIGKILL, then re-inspect until it
    ///     is provably gone or <see cref="KillVerifyBound" /> passes. A signal is sent only while the pid is provably ours.
    /// </summary>
    private async Task<TrainingProcessPresence> TerminateAsync(int target, int identityPid, long expectedStartTicks, CancellationToken cancellationToken)
    {
        var presence = SignalIfSameProcess(target, identityPid, expectedStartTicks, Sigterm);
        if (presence != TrainingProcessPresence.Alive)
        {
            return presence;
        }

        if (await WaitUntilGoneAsync(identityPid, expectedStartTicks, TerminateGrace, cancellationToken).ConfigureAwait(false) == TrainingProcessPresence.Gone)
        {
            return TrainingProcessPresence.Gone;
        }

        presence = SignalIfSameProcess(target, identityPid, expectedStartTicks, Sigkill);
        return presence != TrainingProcessPresence.Alive
            ? presence
            : await WaitUntilGoneAsync(identityPid, expectedStartTicks, KillVerifyBound, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polls until the pid is provably gone or <paramref name="bound" /> passes; returns the last presence read.</summary>
    private async Task<TrainingProcessPresence> WaitUntilGoneAsync(int processId, long expectedStartTicks, TimeSpan bound, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + bound;
        while (true)
        {
            var presence = PresenceOf(_readStat(processId), expectedStartTicks);
            if (presence == TrainingProcessPresence.Gone || _timeProvider.GetUtcNow() >= deadline)
            {
                return presence;
            }

            await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Signals only on a fresh stat read that matches; otherwise returns what the read proves: gone, or unknown.</summary>
    /// <returns><see cref="TrainingProcessPresence.Alive" /> once the signal was sent.</returns>
    private TrainingProcessPresence SignalIfSameProcess(int target, int identityPid, long expectedStartTicks, int signal)
    {
        var read = _readStat(identityPid);
        if (!MaySignal(read, expectedStartTicks))
        {
            var presence = PresenceOf(read, expectedStartTicks);
            _logger.LogWarning("Pid {Pid} is not provably the recorded process ({Presence}); signal {Signal} was not sent.", identityPid, presence, signal);
            return presence;
        }

        // Read-then-signal still leaves a microsecond window; pidfd_open + pidfd_send_signal is the upgrade that closes it.
        _ = _kill(target, signal);
        return TrainingProcessPresence.Alive;
    }

    /// <summary>True only when the stat was read now and carries the recorded start time: an unreadable pid may have been reused.</summary>
    internal static bool MaySignal(TrainingProcessStatRead read, long expectedStartTicks) =>
        read.Stat is { } stat && stat.StartTicks == expectedStartTicks;

    private static TrainingProcessPresence PresenceOf(TrainingProcessStatRead read, long expectedStartTicks) =>
        read.Stat is { } stat && stat.StartTicks != expectedStartTicks ? TrainingProcessPresence.Gone : read.Presence;

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
