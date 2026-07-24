namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Production <see cref="IImageServerProcessLauncher" />: starts a real <c>sd-server</c> child contained for
///     orphan-free tree-kill. On Windows the child is assigned to a Job Object with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>; on Linux it starts a new session/process-group (<c>setsid</c>) so
///     <c>kill(-pgid)</c> reaps every descendant. Mirrors <c>LlamaServerProcessLauncher</c>.
/// </summary>
/// <remarks>
///     The child's stdout/stderr are drained (so a chatty server never stalls on a full pipe) and forwarded to the app
///     logger at <b>Debug</b> level — NOT Information. sd-server can echo the request prompt in its own logs, and prompts
///     are privacy-sensitive: keeping the forward at Debug ensures a normal Information-level deployment never
///     persists a prompt, while a developer can still opt into the backend/device banner at Debug.
/// </remarks>
internal sealed class ImageServerProcessLauncher : IImageServerProcessLauncher
{
    private readonly ILogger<ImageServerProcessLauncher> _logger;

    public ImageServerProcessLauncher(ILogger<ImageServerProcessLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public IImageServerProcessHandle Launch(ImageServerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var label = spec.ModelName;

        if (OperatingSystem.IsWindows())
        {
            return LaunchWindows(BuildStartInfo(spec), label);
        }

        if (OperatingSystem.IsLinux())
        {
            return LaunchLinux(BuildStartInfo(spec), label);
        }

        // macOS / other Unix: no Job Object and no setsid wrapper — a plain process whose own tree-kill tears down the
        // server keeps the launcher functional on the CPU floor elsewhere.
        return LaunchPlain(BuildStartInfo(spec), label);
    }

    private static ProcessStartInfo BuildStartInfo(ImageServerLaunchSpec spec)
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
    private IImageServerProcessHandle LaunchWindows(ProcessStartInfo startInfo, string label)
    {
        var process = StartProcess(startInfo, label);
        return WindowsImageJobObjectProcessHandle.Wrap(process);
    }

    [SupportedOSPlatform("linux")]
    private IImageServerProcessHandle LaunchLinux(ProcessStartInfo startInfo, string label)
    {
        // Run sd-server under `setsid` so it leads a new process group; tree-kill = kill(-pgid). The server inherits
        // setsid's redirected stdout/stderr, so the draining wired in StartProcess still captures the server's output.
        var serverPath = startInfo.FileName;
        startInfo.FileName = SetsidLocator.ResolveAbsolutePath();
        startInfo.ArgumentList.Insert(index: 0, serverPath);

#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return LinuxImageProcessGroupHandle.Wrap(StartProcess(startInfo, label));
#pragma warning restore CA2000
    }

    private IImageServerProcessHandle LaunchPlain(ProcessStartInfo startInfo, string label)
    {
#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return PlainImageProcessHandle.Wrap(StartProcess(startInfo, label));
#pragma warning restore CA2000
    }

    private Process StartProcess(ProcessStartInfo startInfo, string label)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };

        // Drain both streams so the pipes never fill and stall the child; forward at Debug (see remarks — prompt privacy).
        process.OutputDataReceived += (_, e) => ForwardLine(label, e.Data);
        process.ErrorDataReceived += (_, e) => ForwardLine(label, e.Data);

        try
        {
            if (!process.Start())
            {
                throw new StableDiffusionRuntimeException("The image runtime process did not start.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (StableDiffusionRuntimeException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new StableDiffusionRuntimeException("The image runtime could not be started.", ex);
        }

        return process;
    }

    private void ForwardLine(string label, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Debug, not Information: sd-server may echo the prompt; keep it out of the default app log.
        _logger.LogDebug("sd-server[{Label}] {Line}", label, line);
    }
}
