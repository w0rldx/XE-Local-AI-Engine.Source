namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Linux process handle whose tree-kill signals the child's whole process group.
/// </summary>
/// <remarks>
///     The caller starts the child under <c>setsid</c>, so its pid is also its process-group id and <c>kill(-pid)</c>
///     reaps the server plus any descendants it forked — no orphans. Polite first: <c>SIGTERM</c> to the group, a
///     two-second grace, then <c>SIGKILL</c>.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed partial class LinuxProcessGroupHandle : IProcessTreeHandle
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;
    private const int GraceMilliseconds = 2000;

    private readonly ProcessStderrTail? _stderrTail;
    private int _disposed;

    private LinuxProcessGroupHandle(Process process, ProcessStderrTail? stderrTail)
    {
        Process = process;
        _stderrTail = stderrTail;
    }

    /// <summary>The wrapped process, for run-to-completion runners that await exit and read the code while the handle owns disposal and tree-kill.</summary>
    public Process Process { get; }

    public int ProcessId => Process.Id;

    public bool HasExited => ProcessExitState.SafeHasExited(Process);

    public int? ExitCode => ProcessExitState.SafeExitCode(Process);

    public string? StderrTail => _stderrTail?.Snapshot();

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) =>
        ProcessExitState.WaitForExitAsync(Process, timeout, ct);

    public void TreeKill()
    {
        if (ProcessExitState.SafeHasExited(Process))
        {
            return;
        }

        var pgid = Process.Id; // setsid makes the child a group leader: pgid == pid.

        // Polite stop first, then force. A negative pid targets the entire process group.
        _ = Kill(-pgid, Sigterm);
        if (!Process.WaitForExit(GraceMilliseconds))
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
            Process.Dispose();
        }
    }

    /// <summary>Takes ownership of an already-started process, disposing it if the wrap itself throws.</summary>
    public static LinuxProcessGroupHandle Wrap(Process process, ProcessStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new LinuxProcessGroupHandle(process, stderrTail);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    // int kill(pid_t pid, int sig); — a negative pid signals the process group abs(pid).
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Kill(int pid, int sig);
}
