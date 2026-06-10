namespace XE_Local_AI_Engine.Installer.Driver.Windows;

using System.Diagnostics;
using System.Text;

/// <summary>
///     Production <see cref="IProcessRunner" /> over <see cref="Process" />. Captures stdout/stderr and
///     feeds <paramref name="standardInput" /> (used for the hash-pinned <c>bash -s</c> seam — the
///     script body rides stdin). BCL-only so this file compiles on any platform.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessRunResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendLine(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(stderr, e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessRunResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };
    }

    private static void AppendLine(StringBuilder builder, string? data)
    {
        if (data is not null)
        {
            builder.AppendLine(data);
        }
    }
}
