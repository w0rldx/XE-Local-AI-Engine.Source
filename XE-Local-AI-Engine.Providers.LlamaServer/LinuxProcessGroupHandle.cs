namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
///     Linux process handle whose tree-kill signals the child's whole process group. The child is started under
///     <c>setsid</c> (see <see cref="LlamaServerProcessLauncher" />), so its pid is also its process-group id and
///     <c>kill(-pid, SIGKILL)</c> reaps the server plus any descendants it forked — no orphans.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxProcessGroupHandle(Process process) : ILlamaServerProcessHandle
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;

    private readonly Process _process = process ?? throw new ArgumentNullException(nameof(process));
    private int _disposed;

    public int ProcessId => _process.Id;

    public bool HasExited => SafeHasExited(_process);

    public void TreeKill()
    {
        if (SafeHasExited(_process))
        {
            return;
        }

        var pgid = _process.Id; // setsid makes the child a group leader: pgid == pid.

        // Polite stop first, then force. Negative pid targets the entire process group.
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
    public static LinuxProcessGroupHandle Wrap(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new LinuxProcessGroupHandle(process);
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
            return true; // No associated process — treat as exited.
        }
    }

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
