namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Production <see cref="IImageServerProcessLauncher" />: starts a real <c>sd-server</c> child contained for
///     orphan-free tree-kill. On Windows the child is assigned to a Job Object with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>; on Linux it starts a new session/process-group (<c>setsid</c>) so
///     <c>kill(-pgid)</c> reaps every descendant. Mirrors <c>LlamaServerProcessLauncher</c>.
/// </summary>
/// <remarks>
///     <para>
///         The child's stdout/stderr are drained (so a chatty server never stalls on a full pipe) and forwarded to the
///         app logger at <b>Debug</b> level — NOT Information. sd-server can echo the request prompt in its own logs,
///         and prompts are privacy-sensitive: keeping the forward at Debug ensures a normal Information-level deployment
///         never persists a prompt, while a developer can still opt into the backend/device banner at Debug.
///     </para>
///     <para>
///         The same drained output is the ONLY place sd-server reports sampling progress — its HTTP job contract has no
///         step or percent field at all. Each frame is therefore offered to <see cref="SdProgressLineParser" /> and only
///         the PARSED result (phase plus step counters, never the text) is published to
///         <see cref="IImageServerProgressBroker" />, so the prompt that may sit in a log line cannot ride the progress
///         path out to the status hub.
///     </para>
///     <para>
///         Framing is delegated to <see cref="SdOutputFrameSplitter" /> rather than <c>BeginOutputReadLine</c>, which
///         cannot surface sd.cpp's leading-carriage-return progress bar in time — see that type's remarks.
///     </para>
/// </remarks>
internal sealed class ImageServerProcessLauncher : IImageServerProcessLauncher
{
    /// <summary>Drain read size. Bounds one read only; the splitter reassembles frames across reads.</summary>
    private const int DrainBufferLength = 4096;

    private readonly ILogger<ImageServerProcessLauncher> _logger;
    private readonly IImageServerProgressBroker _progressBroker;

    public ImageServerProcessLauncher(ILogger<ImageServerProcessLauncher> logger, IImageServerProgressBroker progressBroker)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(progressBroker);
        _logger = logger;
        _progressBroker = progressBroker;
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

        try
        {
            if (!process.Start())
            {
                throw new StableDiffusionRuntimeException("The image runtime process did not start.");
            }

            // Drain both streams so the pipes never fill and stall the child.
            StartDrain(process.StandardOutput, label);
            StartDrain(process.StandardError, label);
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

    /// <summary>
    ///     Starts the detached drain loop for one of the child's streams. Detached on purpose: the handle owns the
    ///     process lifetime, and the loop ends by itself at EOF when the process exits or is tree-killed.
    /// </summary>
    private void StartDrain(StreamReader reader, string label)
    {
        _ = Task.Run(() => DrainAsync(reader, label));
    }

    /// <summary>
    ///     Reads one stream to EOF, feeding it through the frame splitter. Reads into a char buffer rather than calling
    ///     <c>ReadLineAsync</c>, whose carriage-return handling waits to see whether a line feed follows — the exact
    ///     one-frame stall the splitter exists to avoid.
    /// </summary>
    private async Task DrainAsync(StreamReader reader, string label)
    {
        var buffer = new char[DrainBufferLength];
        var splitter = new SdOutputFrameSplitter(frame => ForwardLine(label, frame));

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                splitter.Append(buffer.AsSpan(start: 0, read));
            }
        }
        catch (Exception exception)
        {
            // The stream dies with the process (a tree-kill closes the pipe mid-read). Losing the tail of the log is
            // expected there and must never surface as an unobserved task exception.
            _logger.LogDebug(exception, "sd-server[{Label}] output drain ended.", label);
        }

        // Whatever the child wrote without a trailing terminator before EOF is still worth forwarding.
        splitter.Flush();
    }

    private void ForwardLine(string label, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Debug, not Information: sd-server may echo the prompt; keep it out of the default app log.
        _logger.LogDebug("sd-server[{Label}] {Line}", label, line);

        // Only the PARSED observation crosses the progress seam — never the line itself.
        if (SdProgressLineParser.TryParse(line, out var observation))
        {
            _progressBroker.Publish(label, observation);
        }
    }
}
