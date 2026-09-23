namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Linux process handle whose tree-kill signals the child's whole process group. The child is started under
///     <c>setsid</c>, so its pid is also its process-group id and <c>kill(-pid, SIGKILL)</c> reaps the server plus
///     anything it forked — no orphans.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxWhisperProcessGroupHandle : IWhisperServerProcessHandle
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;

    private readonly Process _process;
    private readonly WhisperServerStderrTail? _stderrTail;
    private int _disposed;

    public LinuxWhisperProcessGroupHandle(Process process, WhisperServerStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        _stderrTail = stderrTail;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => SafeHasExited(_process);

    public int? ExitCode => SafeExitCode(_process);

    public string? StderrTail => _stderrTail?.Snapshot();

    public void TreeKill()
    {
        if (SafeHasExited(_process))
        {
            return;
        }

        var pgid = _process.Id; // setsid makes the child a group leader: pgid == pid.

        // Polite stop first, then force. A negative pid targets the entire process group.
        _ = Kill(-pgid, Sigterm);
        if (!_process.WaitForExit(2000))
        {
            _ = Kill(-pgid, Sigkill);
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
            TreeKill();
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>Takes ownership of an already-started process, disposing it if the wrap itself throws.</summary>
    public static LinuxWhisperProcessGroupHandle Wrap(Process process, WhisperServerStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new LinuxWhisperProcessGroupHandle(process, stderrTail);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // No associated process — treat as exited.
            return true;
        }
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            // No associated process, or the handle is already disposed: the code is unknown.
            return null;
        }
    }

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
