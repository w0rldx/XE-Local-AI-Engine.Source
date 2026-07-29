namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
///     Linux <see cref="ISandboxProcessGroupKiller" />. Group signalling mirrors <c>LinuxProcessGroupHandle</c> in the
///     llama.cpp provider — the recorded precedent for this shape — with <c>kill(-pgid, …)</c> targeting the whole
///     group: SIGTERM first, then SIGKILL for anything still standing.
///     <para>
///         Liveness and start-time come from <c>/proc</c> rather than <see cref="System.Diagnostics.Process" /> because
///         the reaper needs the start time of a process it does not own, and needs it to be the SAME clock the marker
///         recorded — field 22 of <c>/proc/[pid]/stat</c>, in clock ticks since boot.
///     </para>
/// </summary>
public sealed class LinuxSandboxProcessGroupKiller : ISandboxProcessGroupKiller
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;

    // Grace between the polite and the forceful signal, matching LinuxProcessGroupHandle's 2s.
    private static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimeProvider _timeProvider;

    /// <summary>
    ///     The time source is optional so the provider can construct a killer inline for its best-effort group kill,
    ///     while ActivatorUtilities injects the registered one for the reaper.
    /// </summary>
    public LinuxSandboxProcessGroupKiller(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public long? GetProcessStartTicks(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        string raw;
        try
        {
            raw = File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // No such process, or it exited while being read.
            return null;
        }

        // Field 2 (comm) is the executable name in parentheses and may itself contain spaces or parentheses, so the
        // fields after it are located from the LAST ')' rather than by splitting the whole line. The remaining tokens
        // start at field 3 (state), which puts starttime (field 22) at index 19.
        var commEnd = raw.LastIndexOf(')');
        if (commEnd < 0 || commEnd + 2 >= raw.Length)
        {
            return null;
        }

        var fields = raw[(commEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int startTimeIndex = 19;
        if (fields.Length <= startTimeIndex)
        {
            return null;
        }

        return long.TryParse(fields[startTimeIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            ? ticks
            : null;
    }

    /// <inheritdoc />
    public bool IsProcessAlive(int processId)
    {
        return processId > 0 && Directory.Exists(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}"));
    }

    /// <inheritdoc />
    public async Task KillProcessGroupAsync(int processGroupId, CancellationToken cancellationToken = default)
    {
        if (processGroupId <= 1)
        {
            // Guard against a corrupt marker: 0 would signal the CALLER's process group and -1 every process the user
            // owns. Neither is ever a legitimate sandbox group.
            return;
        }

        // A negative pid signals the entire process group. The child is a group leader (launched under setsid), so this
        // reaches it and every descendant it forked.
        _ = kill(-processGroupId, Sigterm);

        // Poll rather than wait out the whole grace, so a group that dies promptly is not waited on.
        var deadline = _timeProvider.GetUtcNow() + TerminateGrace;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            if (!IsProcessAlive(processGroupId))
            {
                return;
            }

            await Task.Delay(ExitPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        _ = kill(-processGroupId, Sigkill);
    }

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid). DllImport (not the
    // source-generated LibraryImport) keeps this project free of AllowUnsafeBlocks, matching the libc open() import in
    // ProcessSandboxRuntimeProvider.
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int kill(int pid, int sig);
}
