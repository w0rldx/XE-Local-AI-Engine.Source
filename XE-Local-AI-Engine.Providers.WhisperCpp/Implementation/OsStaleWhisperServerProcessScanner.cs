namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Production <see cref="IStaleWhisperServerProcessScanner" />: reads the OS process table for
///     <c>whisper-server</c> processes and tree-kills by pid.
/// </summary>
/// <remarks>
///     On Linux the executable path comes from the <c>/proc/&lt;pid&gt;/exe</c> symlink — the kernel's authoritative
///     pointer to the real binary — rather than from the managed module list, which throws for a process this user
///     does not own. Other platforms read the main module. Every failure yields a null path or an empty list; the
///     reaper is best-effort and this never throws.
/// </remarks>
internal sealed class OsStaleWhisperServerProcessScanner : IStaleWhisperServerProcessScanner
{
    // The OS process name carries no extension on any platform (Windows reports "whisper-server", not the .exe).
    private const string WhisperServerProcessName = "whisper-server";

    /// <inheritdoc />
    public IReadOnlyList<StaleWhisperServerProcess> EnumerateWhisperServerProcesses()
    {
        var results = new List<StaleWhisperServerProcess>();

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(WhisperServerProcessName);
        }
        catch (InvalidOperationException)
        {
            return results;
        }

        foreach (var process in processes)
        {
            try
            {
                results.Add(new StaleWhisperServerProcess(process.Id, ResolveExecutablePath(process)));
            }
            catch (InvalidOperationException)
            {
                // Exited between enumeration and the id/path read — skip it, never abort the whole scan.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    /// <inheritdoc />
    public void KillProcessTree(int pid)
    {
        // Best-effort: the pid may have exited since enumeration, or be foreign-owned. None of that is fatal for a
        // startup reaper, so the specific failures are swallowed and the caller logs the attempt.
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // No such process — already gone.
        }
        catch (InvalidOperationException)
        {
            // Exited between the lookup and the kill.
        }
        catch (Win32Exception)
        {
            // Access denied, or the OS refused the kill.
        }
        catch (NotSupportedException)
        {
            // Platform without tree-kill support.
        }
    }

    private static string? ResolveExecutablePath(Process process)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var procExe = new FileInfo($"/proc/{process.Id.ToString(CultureInfo.InvariantCulture)}/exe");
                return procExe.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch (Win32Exception)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
