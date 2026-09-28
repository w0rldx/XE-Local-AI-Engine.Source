namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Globalization;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>Process identity read out of <c>/proc</c>, canonical paths and <c>kill(2)</c>, for the spawn-receipt reap.</summary>
/// <remarks>Every member is Linux-only and returns null / false on a read failure rather than throwing.</remarks>
internal static partial class LinuxProcFs
{
    /// <summary>Parent pid (field 4), process group (field 5) and start time (field 22) of <c>/proc/[pid]/stat</c>, or null when unreadable.</summary>
    public static ProcessStat? TryReadStat(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsLinux())
        {
            return null;
        }

        string raw;
        try
        {
            // Forced sync: reached from the synchronous startup reaper and scanner contracts; the source is procfs, which never blocks on a device.
#pragma warning disable MA0045
            raw = File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"));
#pragma warning restore MA0045
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Field 2 (comm) is parenthesized and may contain spaces and parentheses, so the rest is located from the LAST ')'. Tokens then start at
        // field 3 (state), putting ppid at index 1, pgrp at index 2 and starttime at index 19.
        var commEnd = raw.LastIndexOf(')');
        if (commEnd < 0 || commEnd + 2 >= raw.Length)
        {
            return null;
        }

        var fields = raw[(commEnd + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int parentIndex = 1;
        const int groupIndex = 2;
        const int startTimeIndex = 19;
        if (fields.Length <= startTimeIndex
            || !int.TryParse(fields[parentIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent)
            || !int.TryParse(fields[groupIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var group)
            || !long.TryParse(fields[startTimeIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startTicks))
        {
            return null;
        }

        return new ProcessStat(parent, group, startTicks);
    }

    /// <summary>
    ///     The executable <c>/proc/[pid]/exe</c> points at, the kernel's canonical path of the running binary, or null when the
    ///     process is gone or the link is unreadable.
    /// </summary>
    public static string? TryReadExecutablePath(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return new FileInfo(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/exe")).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     The canonical path of <paramref name="path" /> with every symlink resolved, the same form the kernel reports through
    ///     <c>/proc/[pid]/exe</c>, or null when it cannot be resolved.
    /// </summary>
    public static string? TryRealPath(string path)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path))
        {
            return null;
        }

        var resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            // realpath(3) with a null buffer mallocs the result, which only libc's own free(3) may release.
            Free(resolved);
        }
    }

    /// <summary>Sends <paramref name="signal" /> to <paramref name="pid" /> (a negative pid is the process group). Returns true when the kernel accepted it.</summary>
    public static bool Signal(int pid, int signal) =>
        OperatingSystem.IsLinux() && Kill(pid, signal) == 0;

    // char *realpath(const char *path, char *resolved_path); — a null resolved_path makes libc allocate the result.
    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial IntPtr RealPath(string path, IntPtr resolvedPath);

    // free(3): releases what realpath(3) allocated.
    [LibraryImport("libc", EntryPoint = "free")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void Free(IntPtr ptr);

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
