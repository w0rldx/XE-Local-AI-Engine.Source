namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;

/// <summary>The exit code and captured stdout of a short command. A timeout or a failed start reports exit code -1 and empty output.</summary>
internal sealed record ProcessCaptureResult(int ExitCode, string Stdout);

/// <summary>
///     Captures (rather than streams) a short command's stdout under a caller-supplied scrubbed environment, bounded by
///     a timeout and tree-killed on timeout or cancellation. The counterpart to <see cref="StreamingProcessRunner" />,
///     which is for the long-running build steps whose output has to reach the log as it happens.
/// </summary>
internal static class ProcessCaptureRunner
{
    /// <summary>
    ///     Runs <paramref name="file" /> <paramref name="args" /> in <paramref name="workDir" /> with
    ///     <paramref name="environment" /> replacing the inherited environment entirely. A timeout tree-kills the
    ///     process and reports exit code -1 rather than throwing; caller cancellation tree-kills and rethrows.
    /// </summary>
    public static async Task<ProcessCaptureResult> RunAsync(string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment,
        string workDir,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Clear();
        foreach (var entry in environment)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        if (!process.Start())
        {
            return new ProcessCaptureResult(ExitCode: -1, string.Empty);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);
            return new ProcessCaptureResult(process.ExitCode, stdout);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessCaptureResult(ExitCode: -1, string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
    }

    /// <summary>Best-effort tree kill: the process can exit between the check and the kill, or the OS can deny the reap.</summary>
    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }
}
