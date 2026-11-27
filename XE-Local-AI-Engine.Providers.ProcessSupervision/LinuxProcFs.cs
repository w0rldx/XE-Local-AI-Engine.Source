namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Globalization;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>Process identity read out of <c>/proc</c>, canonical paths and <c>kill(2)</c>, for the spawn-receipt reap.</summary>
/// <remarks>Every member is Linux-only and returns null / false on a read failure rather than throwing.</remarks>
internal static partial class LinuxProcFs
{
    /// <summary>State (field 3), parent pid (field 4), process group (field 5) and start time (field 22) of <c>/proc/[pid]/stat</c>, or null when unreadable.</summary>
    public static ProcessStat? ReadStatOrNull(int processId)
    {
        if (processId <= 0 || !OperatingSystem.IsLinux())
        {
            return null;
        }

        var read = LinuxProcStat.Read(string.Create(CultureInfo.InvariantCulture, $"/proc/{processId}/stat"));
        return read.Outcome == LinuxProcStatOutcome.Parsed
            ? new ProcessStat(read.ParentProcessId, read.ProcessGroupId, read.StartTicks, read.Exited)
            : null;
    }

    /// <summary>
    ///     The executable <c>/proc/[pid]/exe</c> points at, the kernel's canonical path of the running binary, or null when the
    ///     process is gone or the link is unreadable.
    /// </summary>
    public static string? ReadExecutablePathOrNull(int processId)
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
    public static string? RealPathOrNull(string path)
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

    /// <summary>Sends <paramref name="signal" /> to <paramref name="pid" /> (a negative pid is the process group). Returns 0 when the kernel accepted it, else the errno (ENOSYS off Linux).</summary>
    public static int Signal(int pid, int signal) =>
        LinuxKill.Send(pid, signal);

    // char *realpath(const char *path, char *resolved_path); — a null resolved_path makes libc allocate the result.
    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial IntPtr RealPath(string path, IntPtr resolvedPath);

    // free(3): releases what realpath(3) allocated.
    [LibraryImport("libc", EntryPoint = "free")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void Free(IntPtr ptr);
}
