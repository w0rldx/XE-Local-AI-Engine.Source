namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

using System.Diagnostics;
using System.Text;

public sealed class WindowsProcessRunner : IWindowsProcessRunner
{
    private const int MaxCapturedCharacters = 64 * 1024;

    public async Task<WindowsProcessResult> RunAsync(WindowsProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = CreateProcess(request);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process {request.FileName}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        if (request.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), linked.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            KillBestEffort(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new WindowsProcessResult(process.HasExited ? process.ExitCode : -1,
            Truncate(stdout),
            Truncate(stderr),
            timedOut);
    }

    private static Process CreateProcess(WindowsProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardInput = request.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = false
        };
    }

    private static void KillBestEffort(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException exception)
        {
            _ = exception;
        }
    }

    private static string Truncate(string value)
    {
        return value.Length <= MaxCapturedCharacters
            ? value
            : value[..MaxCapturedCharacters];
    }
}
