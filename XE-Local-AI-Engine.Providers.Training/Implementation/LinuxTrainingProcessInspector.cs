namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Process-group id and start time as read from <c>/proc/[pid]/stat</c>.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TrainingProcessStat(int Pgid, long StartTicks);

/// <summary>Reads trainer-process identity out of <c>/proc</c> and signals process groups.</summary>
/// <remarks>
///     The receipt-validation half of the <c>SandboxOrphanReaper</c> model: identity is proven from several
///     independent fields before anything is signalled, never from an executable-path match alone the way
///     <c>StaleLlamaServerReaper</c> does it.
/// </remarks>
internal sealed partial class LinuxTrainingProcessInspector : ITrainingProcessInspector
{
    /// <summary>The variable the run token travels to the child in, and is read back from, in <c>/proc/[pid]/environ</c>.</summary>
    public const string RunTokenVariable = "XE_TRAINING_RUN_TOKEN";

    private const int Sigterm = 15;
    private const int Sigkill = 9;

    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly Func<int, int, int> _kill;
    private readonly ILogger<LinuxTrainingProcessInspector> _logger;
    private readonly Func<int, TrainingProcessStat?> _readStat;
    private readonly TimeProvider _timeProvider;

    public LinuxTrainingProcessInspector(TimeProvider timeProvider, ILogger<LinuxTrainingProcessInspector> logger)
        : this(timeProvider, logger, TryReadStat, Kill)
    {
    }

    /// <summary>Seam for the identity check: the stat reader and <c>kill(2)</c> are swapped out by tests.</summary>
    internal LinuxTrainingProcessInspector(TimeProvider timeProvider,
        ILogger<LinuxTrainingProcessInspector> logger,
        Func<int, TrainingProcessStat?> readStat,
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

        if (TryReadStat(processId) is not { } stat)
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

    /// <summary>This host's own process group, read live; 0 off Linux.</summary>
    public static int HostProcessGroupId => OperatingSystem.IsLinux() ? GetProcessGroup() : 0;

    int ITrainingProcessInspector.HostProcessGroupId => HostProcessGroupId;

    public Task KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default)
    {
        // 0 is the CALLER's group, -1 every process this user owns, and the host's own group is the node itself.
        if (processGroupId <= 1 || processGroupId == HostProcessGroupId || !OperatingSystem.IsLinux())
        {
            return Task.CompletedTask;
        }

        return TerminateAsync(-processGroupId, processGroupId, expectedStartTicks, cancellationToken);
    }

    public Task KillProcessAsync(int processId, long expectedStartTicks, CancellationToken cancellationToken = default)
    {
        if (processId <= 1 || processId == Environment.ProcessId || !OperatingSystem.IsLinux())
        {
            return Task.CompletedTask;
        }

        return TerminateAsync(processId, processId, expectedStartTicks, cancellationToken);
    }

    /// <summary>
    ///     SIGTERM <paramref name="target" />, wait for <paramref name="identityPid" /> to vanish, then SIGKILL. Each signal is sent
    ///     only while that pid still carries <paramref name="expectedStartTicks" />, so a recycled pid is never signalled.
    /// </summary>
    private async Task TerminateAsync(int target, int identityPid, long expectedStartTicks, CancellationToken cancellationToken)
    {
        if (!SignalIfSameProcess(target, identityPid, expectedStartTicks, Sigterm))
        {
            return;
        }

        var deadline = _timeProvider.GetUtcNow() + TerminateGrace;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            if (!IsSameProcess(identityPid, expectedStartTicks))
            {
                return;
            }

            await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        _ = SignalIfSameProcess(target, identityPid, expectedStartTicks, Sigkill);
    }

    // Read-then-signal still leaves a microsecond window; pidfd_open + pidfd_send_signal is the upgrade that closes it.
    private bool SignalIfSameProcess(int target, int identityPid, long expectedStartTicks, int signal)
    {
        if (!IsSameProcess(identityPid, expectedStartTicks))
        {
            _logger.LogWarning("Pid {Pid} no longer carries the recorded start time {StartTicks}; signal {Signal} was not sent.",
                identityPid, expectedStartTicks, signal);
            return false;
        }

        _ = _kill(target, signal);
        return true;
    }

    private bool IsSameProcess(int processId, long expectedStartTicks) =>
        _readStat(processId) is { } stat && stat.StartTicks == expectedStartTicks;

    /// <summary>Process-group id (field 5) and start time (field 22) of <c>/proc/[pid]/stat</c>, or null when gone.</summary>
    public static TrainingProcessStat? TryReadStat(int processId)
    {
        string raw;
        try
        {
            // Forced sync: reached from the synchronous ITrainingProcessSpawner.Inspect contract and from the spawner's
            // post-start identity read; the source is procfs, which never blocks on a device.
#pragma warning disable MA0045
            raw = File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"));
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Field 2 (comm) is parenthesized and may contain spaces and parentheses, so the rest is located from the LAST
        // ')'. Tokens then start at field 3 (state), putting pgrp at index 2 and starttime at index 19.
        var commEnd = raw.LastIndexOf(')');
        if (commEnd < 0 || commEnd + 2 >= raw.Length)
        {
            return null;
        }

        var fields = raw[(commEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int pgidIndex = 2;
        const int startTimeIndex = 19;
        if (fields.Length <= startTimeIndex
            || !int.TryParse(fields[pgidIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pgid)
            || !long.TryParse(fields[startTimeIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks))
        {
            return null;
        }

        return new TrainingProcessStat(pgid, startTicks);
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
            // Forced sync: same synchronous ITrainingProcessSpawner.Inspect contract as TryReadStat; procfs source.
#pragma warning disable MA0045
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
                       .FirstOrDefault();
    }

    // pid_t getpgrp(void); — the calling process's group, which a trainer must never be signalled through.
    [LibraryImport("libc", EntryPoint = "getpgrp")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetProcessGroup();

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
