namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Diagnostics;
using System.Runtime.Versioning;

/// <summary>
///     Production <see cref="ILlamaServerProcessLauncher" />: starts a real <c>llama-server</c> child contained for
///     orphan-free tree-kill. On Windows the child is assigned to a Job Object with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> so closing the job (handle dispose) terminates the whole tree. On
///     Linux the child starts a new session/process-group (<c>setsid</c>) so <c>kill(-pgid)</c> on teardown reaps every
///     descendant. No cross-OS native calls leak: each containment path is reached only under its OS guard.
/// </summary>
internal sealed class LlamaServerProcessLauncher : ILlamaServerProcessLauncher
{
    /// <inheritdoc />
    public ILlamaServerProcessHandle Launch(LlamaServerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (OperatingSystem.IsWindows())
        {
            return LaunchWindows(BuildStartInfo(spec));
        }

        if (OperatingSystem.IsLinux())
        {
            return LaunchLinux(BuildStartInfo(spec));
        }

        // macOS / other Unix: no Job Object and no setsid wrapper. Supervised GPU inference targets Windows + Linux,
        // which are the only platforms with a dedicated containment primitive; on the CPU floor elsewhere a plain
        // process whose own tree-kill tears down the server keeps the launcher functional.
        return LaunchPlain(BuildStartInfo(spec));
    }

    private static ProcessStartInfo BuildStartInfo(LlamaServerLaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in spec.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    [SupportedOSPlatform("windows")]
    private static ILlamaServerProcessHandle LaunchWindows(ProcessStartInfo startInfo)
    {
        // Wrap takes ownership of the process and disposes it on any containment failure.
        var process = StartProcess(startInfo);
        return WindowsJobObjectProcessHandle.Wrap(process);
    }

    [SupportedOSPlatform("linux")]
    private static ILlamaServerProcessHandle LaunchLinux(ProcessStartInfo startInfo)
    {
        // Run llama-server under `setsid` so it leads a new process group; tree-kill = kill(-pgid).
        var serverPath = startInfo.FileName;
        startInfo.FileName = "setsid";
        startInfo.ArgumentList.Insert(0, serverPath);

#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return LinuxProcessGroupHandle.Wrap(StartProcess(startInfo));
#pragma warning restore CA2000
    }

    private static ILlamaServerProcessHandle LaunchPlain(ProcessStartInfo startInfo)
    {
#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return PlainProcessHandle.Wrap(StartProcess(startInfo));
#pragma warning restore CA2000
    }

    private static Process StartProcess(ProcessStartInfo startInfo)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };
        try
        {
            if (!process.Start())
            {
                throw new LlamaRuntimeException("The local model runtime process did not start.");
            }
        }
        catch (LlamaRuntimeException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new LlamaRuntimeException("The local model runtime could not be started.", ex);
        }

        return process;
    }
}
