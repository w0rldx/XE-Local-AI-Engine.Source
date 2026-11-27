// The Desktop shell compiles this file as linked source under its own namespace (it has no engine references).

#if XE_DESKTOP
namespace XE_Local_AI_Engine.Desktop.Linux;
#else
namespace XE_Local_AI_Engine.Providers.Abstractions;
#endif

using System.Runtime.InteropServices;

/// <summary>The one libc <c>kill(2)</c> import: a numeric signal, where a negative pid names the process group abs(pid).</summary>
public static partial class LinuxKill
{
    private const int Enosys = 38;

    /// <summary>
    ///     Sends <paramref name="signal" /> to <paramref name="pid" />: 0 when the kernel accepted it, else the errno, which also stays in
    ///     <see cref="Marshal.GetLastPInvokeError" />. ENOSYS off Linux.
    /// </summary>
    public static int Send(int pid, int signal)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Enosys;
        }

        return Kill(pid, signal) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
