namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Production <see cref="IStaleImageServerProcessScanner" />: reads the OS process table for <c>sd-server</c>
///     processes and tree-kills by pid via <see cref="Process" />. On Linux the executable path is read from the
///     <c>/proc/&lt;pid&gt;/exe</c> symlink (the kernel's authoritative pointer to the real binary), which is more reliable
///     than <see cref="ProcessModule.FileName" /> — that can throw for a foreign-owned or just-exited process. Other
///     platforms read <see cref="Process.MainModule" />. Mirrors <c>OsStaleLlamaServerProcessScanner</c>.
/// </summary>
internal sealed class OsStaleImageServerProcessScanner : IStaleImageServerProcessScanner
{
    // The OS process name carries no extension on any platform (Windows reports "sd-server", not "sd-server.exe").
    private const string ImageServerProcessName = "sd-server";

    /// <inheritdoc />
    public IReadOnlyList<StaleImageServerProcess> EnumerateImageServerProcesses()
    {
        var results = new List<StaleImageServerProcess>();

        // Matches every sd-server (only ours in practice — the path filter still excludes any unrelated one). A process-
        // table read failure surfaces nothing rather than throwing (the reaper is best-effort).
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ImageServerProcessName);
        }
        catch (InvalidOperationException)
        {
            return results;
        }

        foreach (var process in processes)
        {
            try
            {
                results.Add(new StaleImageServerProcess(process.Id, ResolveExecutablePath(process)));
            }
            catch (InvalidOperationException)
            {
                // The process exited between enumeration and the id/path read — skip it, never abort the whole scan.
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
        // Best-effort: the pid may have exited between enumeration and here, or be foreign-owned (access denied). All of
        // these are non-fatal for a startup reaper, so swallow the specific failures and let the caller log the attempt.
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
            // Access denied / the OS refused the kill.
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
            // /proc/<pid>/exe is a symlink to the real binary; resolving it avoids ProcessModule.FileName, which can
            // throw for a process this user does not own.
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
