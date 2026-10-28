namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     Captures, rather than streams, a short command's stdout under a caller-supplied scrubbed environment, bounded by
///     a timeout and tree-killed on timeout or cancellation.
/// </summary>
/// <remarks>
///     The counterpart to <see cref="StreamingProcessRunner" />, which is for the long-running build steps whose output
///     has to reach the log as it happens.
/// </remarks>
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
            ProcessTreeKill.TryKill(process);
            return new ProcessCaptureResult(ExitCode: -1, string.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProcessTreeKill.TryKill(process);
            throw;
        }
    }
}
