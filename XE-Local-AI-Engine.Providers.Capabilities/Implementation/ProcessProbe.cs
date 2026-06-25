namespace XE_Local_AI_Engine.Providers.Capabilities.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;

/// <summary>
///     Live <see cref="IProcessProbe" />: shells out to a lightweight, ubiquitous tool (e.g. <c>nvidia-smi</c>) and
///     captures its stdout. A missing tool / spawn failure degrades to <see langword="null" /> — never fatal — so the
///     profiler can fall through to the next detection branch.
/// </summary>
internal sealed class ProcessProbe : IProcessProbe
{
    /// <inheritdoc />
    public async Task<ProcessProbeResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process
            {
                StartInfo = startInfo
            };
            if (!process.Start())
            {
                return null;
            }

            var standardOutput = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessProbeResult(process.ExitCode, standardOutput);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Tool missing / not on PATH / permission denied — treat as "not detected", never fatal.
            return null;
        }
    }
}
