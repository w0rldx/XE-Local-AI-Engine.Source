namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
///     Linux process handle whose tree-kill signals the child's whole process group. The child is started under
///     <c>setsid</c> (see <see cref="LinuxTrainingProcessRunner" />), so its pid is also its process-group id and
///     <c>kill(-pid)</c> reaps uv plus everything it forked — a uv install spawns build backends and downloaders, and
///     killing only uv would orphan them. Mirrors the LlamaServer and StableDiffusionCpp handles; each provider owns its
///     own because only <c>SetsidLocator</c> is shared across them.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxTrainingProcessGroupHandle : IDisposable
{
    private const int Sigterm = 15;
    private const int Sigkill = 9;

    private int _disposed;

    private LinuxTrainingProcessGroupHandle(Process process)
    {
        Process = process;
    }

    public Process Process { get; }

    public void TreeKill()
    {
        if (SafeHasExited(Process))
        {
            return;
        }

        var pgid = Process.Id; // setsid makes the child a group leader: pgid == pid.

        // Polite stop first, then force. A negative pid targets the entire process group.
        _ = Kill(-pgid, Sigterm);
        if (!Process.WaitForExit(2000))
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
    public static LinuxTrainingProcessGroupHandle Wrap(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            return new LinuxTrainingProcessGroupHandle(process);
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
