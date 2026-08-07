namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

internal interface IStableDiffusionSourceCommandRunner
{
    Task<StableDiffusionSourceCommandResult> RunAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct);
}

internal sealed record StableDiffusionSourceCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class StableDiffusionSourceCommandRunner : IStableDiffusionSourceCommandRunner
{
    private const int MaxCapturedChars = 64 * 1024;
    private const int MaxStreamLineChars = 1000;

    public async Task<StableDiffusionSourceCommandResult> RunAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onOutput,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        StableDiffusionSourceProcessHardening.Configure(process.StartInfo, FindIsolationRoot(workingDirectory));
        process.Start();
        StableDiffusionSourceProcessHardening.CloseStandardInput(process);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stdoutTask = DrainAsync(process.StandardOutput, stdout, onOutput, captureOutput, timeoutCts.Token);
        var stderrTask = DrainAsync(process.StandardError, stderr, onOutput, captureOutput, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            await IgnoreCancellationAsync(stdoutTask).ConfigureAwait(false);
            await IgnoreCancellationAsync(stderrTask).ConfigureAwait(false);
            throw new TimeoutException($"The source-build command '{Path.GetFileName(fileName)}' exceeded its time limit.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            await IgnoreCancellationAsync(stdoutTask).ConfigureAwait(false);
            await IgnoreCancellationAsync(stderrTask).ConfigureAwait(false);
            throw;
        }

        return new StableDiffusionSourceCommandResult(process.ExitCode,
            stdout.ToString(),
            stderr.ToString());
    }

    private static string FindIsolationRoot(string workingDirectory)
    {
        for (var current = new DirectoryInfo(workingDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ".build-in-progress")))
            {
                return current.FullName;
            }
        }

        return workingDirectory;
    }

    private static async Task DrainAsync(StreamReader reader,
        StringBuilder destination,
        Action<string> onOutput,
        bool captureOutput,
        CancellationToken ct)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                var value = buffer[index];
                if (captureOutput && destination.Length < MaxCapturedChars)
                {
                    destination.Append(value);
                }

                if (value == '\n')
                {
                    onOutput(line.ToString());
                    line.Clear();
                }
                else if (value != '\r' && line.Length < MaxStreamLineChars)
                {
                    line.Append(value);
                }
            }
        }

        if (line.Length > 0)
        {
            onOutput(line.ToString());
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The command token was cancelled and the process tree was killed.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(milliseconds: 5000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Best-effort process-tree cancellation.
        }
    }
}
