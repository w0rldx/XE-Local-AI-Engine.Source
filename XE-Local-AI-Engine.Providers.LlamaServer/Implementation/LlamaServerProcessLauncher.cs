namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Production <see cref="ILlamaServerProcessLauncher" />: starts a real <c>llama-server</c> child, contained for
///     orphan-free tree-kill.
/// </summary>
/// <remarks>
///     On Windows the child is assigned to a Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>, so disposing
///     the job handle terminates the whole tree; on Linux it starts a new session and process group (<c>setsid</c>),
///     so <c>kill(-pgid)</c> on teardown reaps every descendant, and no cross-OS native call leaks because each path
///     is reached only under its own OS guard. Its stdout and stderr are redirected and forwarded line by line to the
///     app logger, which is what makes GPU-offload behaviour diagnosable, and draining them cannot stall the child.
/// </remarks>
internal sealed class LlamaServerProcessLauncher : ILlamaServerProcessLauncher
{
    private readonly ILogger<LlamaServerProcessLauncher> _logger;

    public LlamaServerProcessLauncher(ILogger<LlamaServerProcessLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public ILlamaServerProcessHandle Launch(LlamaServerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Stable label for every forwarded log line so concurrent (model, role) servers are distinguishable.
        var label = $"{spec.ModelName}/{spec.Role}";

        // Optional per-line capture sink — set only for operator profiling spawns; null for every normal spawn.
        var capture = spec.StartupCapture;

        // Null for every operator-driven spawn, so those keep logging at Information exactly as before.
        var demote = spec.ShouldDemoteForwardedLines;

        if (OperatingSystem.IsWindows())
        {
            return LaunchWindows(BuildStartInfo(spec), label, capture, demote);
        }

        if (OperatingSystem.IsLinux())
        {
            return LaunchLinux(BuildStartInfo(spec), label, capture, demote);
        }

        // macOS and other Unix: no Job Object and no setsid wrapper, supervised GPU inference targeting Windows and Linux, the only platforms with a dedicated
        // containment primitive. On the CPU floor elsewhere a plain process whose own tree-kill tears down the server keeps the launcher functional.
        return LaunchPlain(BuildStartInfo(spec), label, capture, demote);
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
    private ILlamaServerProcessHandle LaunchWindows(ProcessStartInfo startInfo, string label, Action<string>? capture, Func<bool>? demote)
    {
        // Wrap takes ownership of the process and disposes it on any containment failure.
        var process = StartProcess(startInfo, label, capture, demote);
        return WindowsJobObjectProcessHandle.Wrap(process);
    }

    [SupportedOSPlatform("linux")]
    private ILlamaServerProcessHandle LaunchLinux(ProcessStartInfo startInfo, string label, Action<string>? capture, Func<bool>? demote)
    {
        // Run llama-server under `setsid` so it leads a new process group; tree-kill = kill(-pgid). The server inherits
        // setsid's redirected stdout/stderr, so the forwarding wired in StartProcess still captures the server's output.
        var serverPath = startInfo.FileName;
        startInfo.FileName = SetsidLocator.ResolveAbsolutePath();
        startInfo.ArgumentList.Insert(index: 0, serverPath);

#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return LinuxProcessGroupHandle.Wrap(StartProcess(startInfo, label, capture, demote));
#pragma warning restore CA2000
    }

    private ILlamaServerProcessHandle LaunchPlain(ProcessStartInfo startInfo, string label, Action<string>? capture, Func<bool>? demote)
    {
#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return PlainProcessHandle.Wrap(StartProcess(startInfo, label, capture, demote));
#pragma warning restore CA2000
    }

    private Process StartProcess(ProcessStartInfo startInfo, string label, Action<string>? capture, Func<bool>? demote)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };

        // Forward both streams to the app log and, for profiling spawns, the optional capture sink. Attached before Start, as the Process API requires, and pumped
        // through the async begin-read APIs, so the pipes are drained continuously and never stall the child.
        process.OutputDataReceived += (_, e) => ForwardLine(label, e.Data, capture, demote);
        process.ErrorDataReceived += (_, e) => ForwardLine(label, e.Data, capture, demote);

        try
        {
            if (!process.Start())
            {
                throw new LlamaRuntimeException("The local model runtime process did not start.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
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

    // A line is logged at Information, the level the desktop console surfaces, so the llama.cpp backend banner and model-load summary reach the default app log. The
    // final end-of-stream callback carries null Data, and a capture sink is invoked AFTER logging, both pipes calling this concurrently so the sink must be thread-safe.
    private void ForwardLine(string label, string? line, Action<string>? capture, Func<bool>? demote)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var level = demote is not null && demote() ? LogLevel.Debug : LogLevel.Information;
        _logger.Log(level, "llama-server[{Label}] {Line}", label, line);
        capture?.Invoke(line);
    }
}
