namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Process-group id and start time as read from <c>/proc/[pid]/stat</c>.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TrainingProcessStat(int Pgid, long StartTicks);

/// <summary>
///     Reads trainer-process identity out of <c>/proc</c> and signals process groups. The receipt-validation half of the
///     <c>SandboxOrphanReaper</c> model: identity is proven from several independent fields before anything is
///     signalled, never from an executable-path match alone the way <c>StaleLlamaServerReaper</c> does it.
/// </summary>
internal sealed partial class LinuxTrainingProcessInspector(TimeProvider? timeProvider = null) : ITrainingProcessInspector
{
    /// <summary>The variable the run token travels to the child in, and is read back from, in <c>/proc/[pid]/environ</c>.</summary>
    public const string RunTokenVariable = "XE_TRAINING_RUN_TOKEN";

    private const int Sigterm = 15;
    private const int Sigkill = 9;

    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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

        return new TrainingProcessFacts(stat.Pgid, stat.StartTicks, ResolveExecutablePath(processId), ReadRunToken(processId));
    }

    public async Task KillProcessGroupAsync(int processGroupId, CancellationToken cancellationToken = default)
    {
        if (processGroupId <= 1 || !OperatingSystem.IsLinux())
        {
            // 0 would signal the CALLER's group and -1 every process this user owns. Neither is ever a trainer group.
            return;
        }

        _ = Kill(-processGroupId, Sigterm);
        var deadline = _timeProvider.GetUtcNow() + TerminateGrace;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            if (!Directory.Exists(string.Create(CultureInfo.InvariantCulture, $"/proc/{processGroupId}")))
            {
                return;
            }

            await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        _ = Kill(-processGroupId, Sigkill);
    }

    /// <summary>Process-group id (field 5) and start time (field 22) of <c>/proc/[pid]/stat</c>, or null when gone.</summary>
    public static TrainingProcessStat? TryReadStat(int processId)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Field 2 (comm) is parenthesized and may itself contain spaces and parentheses, so everything after it is
        // located from the LAST ')'. The remaining tokens start at field 3 (state), putting pgrp (field 5) at index 2
        // and starttime (field 22) at index 19.
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
            raw = File.ReadAllBytes(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/environ"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        const string prefix = RunTokenVariable + "=";
        return System.Text.Encoding.UTF8.GetString(raw)
                     .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .Where(static entry => entry.StartsWith(prefix, StringComparison.Ordinal))
                     .Select(static entry => entry[prefix.Length..])
                     .FirstOrDefault();
    }

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
