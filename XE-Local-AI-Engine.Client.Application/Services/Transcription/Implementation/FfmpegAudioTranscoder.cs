namespace XE_Local_AI_Engine.Client.Services.Transcription.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>
///     Default <see cref="IAudioTranscoder" />: shells out to <c>ffmpeg</c> to produce 16 kHz mono WAV.
/// </summary>
/// <remarks>
///     <c>ffmpeg</c> is resolved once, at construction, by walking <c>PATH</c> — mirroring
///     <c>WhisperFfmpegProbe.ResolveFromPath</c>, which is private to the whisper.cpp provider — so an operator who
///     installs it while the node runs gets the capability at the next restart. Nothing a caller sends reaches the
///     argument list: both paths are server-generated <see cref="Guid" /> names travelling through
///     <see cref="ProcessStartInfo.ArgumentList" /> with no shell. See docs/wiki/24-audio-transcription.md ("The transcoder's ffmpeg process").
/// </remarks>
public sealed class FfmpegAudioTranscoder : IAudioTranscoder
{
    // Enough of ffmpeg's stderr to explain a refusal, and little enough that a pathological file cannot flood a log.
    private const int MaxStderrChars = 2000;
    private const int SanitizedStderrChars = 400;
    private const string NoReasonReported = "the converter reported no reason.";

    // How long a killed child is given to actually be gone before the cancellation is reported anyway.
    private const int KillWaitSeconds = 10;

    private readonly string? _executablePath;
    private readonly ILogger<FfmpegAudioTranscoder> _logger;

    public FfmpegAudioTranscoder(ILogger<FfmpegAudioTranscoder> logger)
        : this(logger, ResolveFromPath())
    {
    }

    /// <summary>Takes the resolved executable instead of probing <c>PATH</c> for it.</summary>
    /// <remarks>
    ///     The seam a test needs to drive the cancellation path against a child process it controls — one that can be
    ///     made to outlive the kill — without ffmpeg being installed and without a real conversion. The public
    ///     constructor still probes, so <see cref="IsAvailable" /> keeps its meaning on the production path.
    /// </remarks>
    internal FfmpegAudioTranscoder(ILogger<FfmpegAudioTranscoder> logger, string? executablePath)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _executablePath = executablePath;
    }

    public bool IsAvailable => _executablePath is not null;

    public async Task<string> ToWav16kMonoAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (_executablePath is null)
        {
            throw new AudioTranscodeException("This node cannot convert that audio format because ffmpeg is not installed.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in BuildArguments(sourcePath, destinationPath))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            _ = process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new AudioTranscodeException("This node could not start the audio converter.", exception);
        }

        // A converter that decides to ask a question reads end-of-file and fails at once instead of hanging.
        process.StandardInput.Close();

        // Both pipes are drained concurrently with the wait: a child that fills one and is never read deadlocks.
        // Neither reader throws — each returns what it captured — so both are safe to await after a kill, unfaulted.
        var stderrTask = ReadBoundedAsync(process.StandardError, cancellationToken);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitAfterKillAsync(process);
            _ = await stderrTask;
            _ = await stdoutTask;
            throw;
        }

        var stderr = await stderrTask;
        _ = await stdoutTask;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Audio conversion failed with exit code {ExitCode}.", process.ExitCode);
            throw new AudioTranscodeException($"The audio could not be converted: {SanitizeTail(stderr)}");
        }

        return destinationPath;
    }

    // Exposed for the argument-shape assertion; the command is part of the contract, not an implementation detail.
    internal static IReadOnlyList<string> BuildArguments(string sourcePath, string destinationPath) =>
    [
        "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
        "-i", sourcePath,
        "-ar", "16000",
        "-ac", "1",
        "-f", "wav",
        destinationPath
    ];

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var captured = new StringBuilder();
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (captured.Length < MaxStderrChars)
                {
                    _ = captured.Append(buffer, 0, Math.Min(read, MaxStderrChars - captured.Length));
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The caller is already unwinding, or the kill tore the pipe down mid-read: whatever was captured is
            // enough. This reader must never throw — it is awaited after the kill precisely so its task is observed.
        }

        return captured.ToString();
    }

    /// <summary>
    ///     Reduces ffmpeg's stderr to one display-safe sentence carrying no filesystem path.
    /// </summary>
    /// <remarks>
    ///     With <c>-hide_banner -loglevel error</c> every arriving line is already an error and the last is the most
    ///     specific — the earlier ones are the demuxer's guesses. ffmpeg writes it as <c>&lt;absolute input path&gt;:
    ///     Invalid data found when processing input</c>; the path is engine-generated but still spells out the node's
    ///     data directory, and the string is persisted into the encrypted <c>error_message</c> column and returned on
    ///     the wire, so the path is dropped rather than shortened. Internal so the sanitization can be asserted.
    /// </remarks>
    internal static string SanitizeTail(string stderr)
    {
        var lines = (stderr ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return NoReasonReported;
        }

        var tail = lines[^1];

        // "<path>: <reason>" — keep the reason. The last separator wins, because a Windows path contains one of its own.
        var separator = tail.LastIndexOf(": ", StringComparison.Ordinal);
        if (separator >= 0 && LooksLikeAPath(tail[..separator]))
        {
            tail = tail[(separator + 2)..];
        }

        // Belt and braces for the formats that put the path elsewhere in the line, or name more than one.
        var words = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(static word => !LooksLikeAPath(word));
        var sanitized = string.Join(' ', words);
        if (sanitized.Length == 0)
        {
            return NoReasonReported;
        }

        return sanitized.Length > SanitizedStderrChars ? sanitized[^SanitizedStderrChars..] : sanitized;
    }

    private static bool LooksLikeAPath(string value) =>
        value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal);

    /// <summary>
    ///     Waits for the killed child to be gone, on a bound the caller's cancellation cannot reach.
    /// </summary>
    /// <remarks>
    ///     A kill is a signal, not an exit, and awaiting the two pipe readers proves nothing about it: both use the
    ///     CALLER's token, so on cancellation each returns at once instead of reading to end-of-stream. Without this
    ///     wait the method rethrows while the child may still be writing, and the upload slot's disposal then runs
    ///     against a live writer — on Windows that delete fails, is swallowed by design, and the operator's audio
    ///     survives the request. The bound exists because an unkillable child would otherwise park the request forever.
    /// </remarks>
    private async Task WaitForExitAfterKillAsync(Process process)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(KillWaitSeconds));
        try
        {
            await process.WaitForExitAsync(bound.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            // The child outlived the bound, or there is no handle left to wait on. Neither may replace the
            // OperationCanceledException the caller is unwinding with, so this is reported and nothing else.
            _logger.LogWarning("The audio converter did not exit within {Seconds}s of being killed; a temporary file may survive this request.",
                KillWaitSeconds);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception or AggregateException)
        {
            // The child already exited, the platform refused the kill, or a descendant survived the tree kill (an
            // AggregateException). None may replace the propagating OperationCanceledException, and nothing else is left.
        }
    }

    // Mirrors WhisperFfmpegProbe.ResolveFromPath in the whisper.cpp provider, which is internal to that project.
    private static string? ResolveFromPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, fileName);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry cannot hold an executable; skip it rather than failing the whole probe.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
