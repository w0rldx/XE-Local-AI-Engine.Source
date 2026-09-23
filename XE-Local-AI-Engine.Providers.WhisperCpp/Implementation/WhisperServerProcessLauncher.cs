namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Production <see cref="IWhisperServerProcessLauncher" />: starts a real <c>whisper-server</c> child contained for
///     orphan-free tree-kill.
/// </summary>
/// <remarks>
///     Windows contains the child in a kill-on-close Job Object; Linux starts it under <c>setsid</c> so <c>kill(-pgid)</c> reaps
///     every descendant. Both streams are drained so a full pipe never stalls it, stderr also feeds a bounded tail for crash reports,
///     and every line goes to <b>Debug</b> only, because the server echoes each request's file name. Framing is plain line reading:
///     whisper-server writes no carriage-return progress bar.
/// </remarks>
internal sealed class WhisperServerProcessLauncher : IWhisperServerProcessLauncher
{
    private readonly ILogger<WhisperServerProcessLauncher> _logger;

    public WhisperServerProcessLauncher(ILogger<WhisperServerProcessLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public IWhisperServerProcessHandle Launch(WhisperServerLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var label = spec.ModelId;
        var stderrTail = new WhisperServerStderrTail();

        if (OperatingSystem.IsWindows())
        {
            return LaunchWindows(BuildStartInfo(spec), label, stderrTail);
        }

        if (OperatingSystem.IsLinux())
        {
            return LaunchLinux(BuildStartInfo(spec), label, stderrTail);
        }

        // macOS and other Unix: no Job Object and no setsid wrapper — a plain process whose own tree-kill tears down
        // the server keeps the launcher functional on the CPU floor elsewhere.
        return LaunchPlain(BuildStartInfo(spec), label, stderrTail);
    }

    private static ProcessStartInfo BuildStartInfo(WhisperServerLaunchSpec spec)
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
    private IWhisperServerProcessHandle LaunchWindows(ProcessStartInfo startInfo, string label, WhisperServerStderrTail stderrTail)
    {
        var process = StartProcess(startInfo, label, stderrTail);
        return WindowsWhisperJobObjectProcessHandle.Wrap(process, stderrTail);
    }

    [SupportedOSPlatform("linux")]
    private IWhisperServerProcessHandle LaunchLinux(ProcessStartInfo startInfo, string label, WhisperServerStderrTail stderrTail)
    {
        // Run whisper-server under `setsid` so it leads a new process group; tree-kill is then kill(-pgid). The server
        // inherits setsid's redirected stdout/stderr, so the draining wired in StartProcess still captures its output.
        var serverPath = startInfo.FileName;
        startInfo.FileName = SetsidLocator.ResolveAbsolutePath();
        startInfo.ArgumentList.Insert(index: 0, serverPath);

#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return LinuxWhisperProcessGroupHandle.Wrap(StartProcess(startInfo, label, stderrTail), stderrTail);
#pragma warning restore CA2000
    }

    private IWhisperServerProcessHandle LaunchPlain(ProcessStartInfo startInfo, string label, WhisperServerStderrTail stderrTail)
    {
#pragma warning disable CA2000 // The returned handle takes ownership of the process and disposes it on tree-kill; Wrap disposes on a construction failure.
        return PlainWhisperProcessHandle.Wrap(StartProcess(startInfo, label, stderrTail), stderrTail);
#pragma warning restore CA2000
    }

    private Process StartProcess(ProcessStartInfo startInfo, string label, WhisperServerStderrTail stderrTail)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                throw new WhisperRuntimeException("The transcription runtime process did not start.");
            }

            // Drain both streams so the pipes never fill and stall the child.
            StartDrain(process.StandardOutput, label, stderrTail: null);
            StartDrain(process.StandardError, label, stderrTail);
        }
        catch (WhisperRuntimeException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new WhisperRuntimeException("The transcription runtime could not be started.", ex);
        }

        return process;
    }

    /// <summary>
    ///     Starts the detached drain loop for one of the child's streams. Detached on purpose: the handle owns the
    ///     process lifetime, and the loop ends by itself at EOF when the process exits or is tree-killed.
    /// </summary>
    private void StartDrain(StreamReader reader, string label, WhisperServerStderrTail? stderrTail)
    {
        _ = Task.Run(() => DrainAsync(reader, label, stderrTail), CancellationToken.None);
    }

    private async Task DrainAsync(StreamReader reader, string label, WhisperServerStderrTail? stderrTail)
    {
        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    // Debug, not Information: the server echoes each request's multipart file name.
                    _logger.LogDebug("whisper-server[{Label}] {Line}", label, line);
                    stderrTail?.Append(line);
                }
            }
        }
        catch (Exception exception)
        {
            // The stream dies with the process (a tree-kill closes the pipe mid-read). Losing the tail of the log is
            // expected there and must never surface as an unobserved task exception.
            _logger.LogDebug(exception, "whisper-server[{Label}] output drain ended.", label);
        }
    }
}
