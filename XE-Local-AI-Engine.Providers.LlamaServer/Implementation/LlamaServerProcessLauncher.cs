namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Production <see cref="ILlamaServerProcessLauncher" />: starts a real <c>llama-server</c> child contained for
///     orphan-free tree-kill. On Windows the child is assigned to a Job Object with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> so closing the job (handle dispose) terminates the whole tree. On
///     Linux the child starts a new session/process-group (<c>setsid</c>) so <c>kill(-pgid)</c> on teardown reaps every
///     descendant. No cross-OS native calls leak: each containment path is reached only under its OS guard.
/// </summary>
/// <remarks>
///     The child's stdout/stderr are redirected and forwarded line-by-line to the application logger. llama.cpp prints
///     its backend/device init banner (e.g. <c>ggml_cuda_init: found N CUDA devices</c> or a CUDA DLL load failure) and
///     the per-model load/offload summary to these streams, so forwarding them makes GPU-offload behavior diagnosable
///     from the normal app log instead of requiring the operator to run the server by hand. Draining the pipes also
///     avoids a full-buffer stall on a chatty server (redirected streams that are never read can block the child).
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

        // macOS / other Unix: no Job Object and no setsid wrapper. Supervised GPU inference targets Windows + Linux,
        // which are the only platforms with a dedicated containment primitive; on the CPU floor elsewhere a plain
        // process whose own tree-kill tears down the server keeps the launcher functional.
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

        // Forward both streams to the app log (and, for profiling spawns, the optional capture sink). Attached before
        // Start (per Process API) and pumped via the async begin-read APIs so the pipes are drained continuously and
        // never stall the child.
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

    // A line is logged at Information so the llama.cpp backend/device banner and model-load summary are visible in the
    // default app log (the level the desktop console surfaces). The final end-of-stream callback carries null Data.
    // When set, the capture sink is invoked AFTER logging, in addition to it — both pipes call this concurrently, so
    // the sink the supervisor supplies is responsible for being thread-safe.
    //
    // The one exception is a child whose verbosity the supervisor raised for its own layer-placement measurement: once
    // that child is serving, its extra per-request chatter is demoted to Debug. The sniffer already read those lines in
    // process, so persisting them would inflate the serving log for a diagnostic the operator never asked for. The load
    // window is deliberately NOT demoted, so the placement banner and every failure message still reach Information.
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
