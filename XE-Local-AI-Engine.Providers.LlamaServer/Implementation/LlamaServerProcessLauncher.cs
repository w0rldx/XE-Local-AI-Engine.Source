namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

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
internal sealed partial class LlamaServerProcessLauncher : ILlamaServerProcessLauncher
{
    // At most one slot-module line per process is promoted to Information in this window.
    internal const long PromotedLineIntervalMilliseconds = 5000;

    private readonly ILogger<LlamaServerProcessLauncher> _logger;
    private readonly ChildProcessOutputTailRegistry _outputTails;

    public LlamaServerProcessLauncher(ILogger<LlamaServerProcessLauncher> logger, ChildProcessOutputTailRegistry? outputTails = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _outputTails = outputTails ?? new ChildProcessOutputTailRegistry();
    }

    /// <inheritdoc />
    public IProcessTreeHandle Launch(LlamaServerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // Stable label for every forwarded log line so concurrent (model, role) servers are distinguishable.
        var label = $"{spec.ModelName}/{spec.Role}";

        // Optional per-line capture sink — set only for operator profiling spawns; null for every normal spawn.
        var capture = spec.StartupCapture;

        if (OperatingSystem.IsWindows())
        {
            return LaunchWindows(BuildStartInfo(spec), label, capture);
        }

        if (OperatingSystem.IsLinux())
        {
            return LaunchLinux(BuildStartInfo(spec), label, capture);
        }

        // macOS and other Unix: no Job Object and no setsid wrapper, supervised GPU inference targeting Windows and Linux, the only platforms with a dedicated
        // containment primitive. On the CPU floor elsewhere a plain process whose own tree-kill tears down the server keeps the launcher functional.
        return LaunchPlain(BuildStartInfo(spec), label, capture);
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
    private IProcessTreeHandle LaunchWindows(ProcessStartInfo startInfo, string label, Action<string>? capture)
    {
        // Wrap takes ownership of the process and disposes it on any containment failure.
#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a containment failure.
        var process = StartProcess(startInfo, label, capture);
        return WindowsJobObjectProcessHandle.Wrap(process,
            static ex => new LlamaRuntimeException("The local model runtime could not be contained for safe shutdown.", ex));
#pragma warning restore CA2000
    }

    [SupportedOSPlatform("linux")]
    private IProcessTreeHandle LaunchLinux(ProcessStartInfo startInfo, string label, Action<string>? capture)
    {
        // Run llama-server under `setsid` so it leads a new process group; tree-kill = kill(-pgid). The server inherits
        // setsid's redirected stdout/stderr, so the forwarding wired in StartProcess still captures the server's output.
        var serverPath = startInfo.FileName;
        startInfo.FileName = SetsidLocator.ResolveAbsolutePath();
        startInfo.ArgumentList.Insert(index: 0, serverPath);

#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return LinuxProcessGroupHandle.Wrap(StartProcess(startInfo, label, capture));
#pragma warning restore CA2000
    }

    private IProcessTreeHandle LaunchPlain(ProcessStartInfo startInfo, string label, Action<string>? capture)
    {
#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return PlainProcessHandle.Wrap(StartProcess(startInfo, label, capture));
#pragma warning restore CA2000
    }

    private Process StartProcess(ProcessStartInfo startInfo, string label, Action<string>? capture)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };

        var lastPromoted = new StrongBox<long>(-PromotedLineIntervalMilliseconds);
        var tail = _outputTails.Register($"llama-server[{label}]");

        // Forward both streams to the log, the output tail and the optional capture sink, attached before Start and pumped by the begin-read APIs so the
        // pipes never stall the child. stderr's end-of-stream (null Data) stamps the tail exited: the pipe closes once the whole tree is gone.
        process.OutputDataReceived += (_, e) => ForwardLine(label, e.Data, tail, capture, lastPromoted);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                _outputTails.MarkExited(tail);
                return;
            }

            ForwardLine(label, e.Data, tail, capture, lastPromoted);
        };

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
            _outputTails.MarkExited(tail);
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _outputTails.MarkExited(tail);
            process.Dispose();
            throw new LlamaRuntimeException("The local model runtime could not be started.", ex);
        }

        return process;
    }

    // Every line reaches the tail; only markers and the rate-limited slot line are logged at Information (what the desktop console shows), the rest at
    // Debug. The capture sink runs AFTER logging, from both pipes concurrently, so it must be thread-safe.
    private void ForwardLine(string label, string? line, ProcessStderrTail tail, Action<string>? capture, StrongBox<long> lastPromoted)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        tail.Append(line);
        var promoted = IsMarkerLine(line) || TryPromoteServingLine(line, Environment.TickCount64, ref lastPromoted.Value);
        _logger.Log(promoted ? LogLevel.Information : LogLevel.Debug, "llama-server[{Label}] {Line}", label, line);
        capture?.Invoke(line);
    }

    /// <summary>Whether a forwarded line is a marker kept at Information: errors, warnings, CUDA, backend, load summary and readiness lines.</summary>
    internal static bool IsMarkerLine(string line) =>
        MarkerLineRegex().IsMatch(line);

    /// <summary>
    ///     Whether a non-marker line is kept at Information: only <c>slot</c>-module lines (prompt progress, timings, release), at most one per
    ///     <see cref="PromotedLineIntervalMilliseconds" /> per process.
    /// </summary>
    /// <remarks>
    ///     Demoting every serving-time line hid a long turn's progress from the default log, so a server busy on a long prompt looked hung. The
    ///     request/queue chatter the raised verbosity adds stays at Debug. Thread-safe: both pipes race on <paramref name="lastPromotedMilliseconds" />.
    /// </remarks>
    internal static bool TryPromoteServingLine(string line, long nowMilliseconds, ref long lastPromotedMilliseconds)
    {
        if (!SlotLineRegex().IsMatch(line))
        {
            return false;
        }

        var last = Volatile.Read(ref lastPromotedMilliseconds);
        return nowMilliseconds - last >= PromotedLineIntervalMilliseconds
               && Interlocked.CompareExchange(ref lastPromotedMilliseconds, nowMilliseconds, last) == last;
    }

    // The module token is the first word of a bare line, or follows llama.cpp's "<elapsed> <level>" prefix that -lv adds
    // ("0.06.876.442 I slot   load_model: ..."). Anchored, so "srv  update_slots" or a path containing "slot" never matches.
    [GeneratedRegex(@"^\s*(?:\S+\s+[A-Z]\s+)?slot\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SlotLineRegex();

    // Substring markers, case-insensitive. "warn" also covers "warning", "fail" covers "failed".
    [GeneratedRegex(@"error|warn|fail|cuda|out of memory|ggml_backend|load_tensors|offloaded|print_info: model type|print_info: file size|listening|all slots are idle",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkerLineRegex();
}
