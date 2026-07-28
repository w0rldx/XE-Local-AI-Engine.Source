namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
///     Shared runner for a short-lived <c>llama-server --list-devices</c> probe. Both the process-budget probe
///     (<see cref="LlamaListDevicesProcessVramBudgetProbe" />) and the device-inventory probe
///     (<see cref="LlamaDeviceInventoryProbe" />) ask llama.cpp the same question — "what devices does THIS binary
///     enumerate?" — so the process launch + pipe draining + bounded wait lives here once rather than being duplicated.
/// </summary>
/// <remarks>
///     Unlike the supervised server, <c>--list-devices</c> is a run-to-exit probe, so a plain <see cref="Process" /> with
///     both pipes drained and a bounded wait is sufficient — no Job Object / setsid containment. The working directory is
///     co-located with the binary so its bundled runtime libraries (cudart, vulkan, ggml) resolve, mirroring the launcher.
/// </remarks>
internal static class LlamaListDevicesProcessRunner
{
    /// <summary>
    ///     Runs <c>&lt;executablePath&gt; --list-devices</c> to exit, draining stdout AND stderr (llama.cpp writes the
    ///     device table to one and its backend banner to the other), bounded by <paramref name="timeout" />. Returns the
    ///     combined output, or <see langword="null" /> on a failed start or a timeout overrun. Genuine caller
    ///     cancellation (<paramref name="ct" />) is honored and re-thrown; a timeout is not (it degrades to null).
    /// </summary>
    internal static async Task<string?> RunAsync(string executablePath, TimeSpan timeout, ILogger logger, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,

                // Co-locate the working dir with the binary so its bundled runtime libraries resolve (mirrors the launcher).
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--list-devices");

        if (!process.Start())
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            // Drain BOTH pipes concurrently: an undrained redirected pipe can stall the child. Combine both before
            // parsing so the device column is found regardless of which stream a given build writes it to.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return string.Concat(stdout, "\n", stderr);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The probe overran its budget (the caller's own token did NOT fire). Degrade to null; the child is killed
            // in the finally below so no orphan survives.
            logger.LogWarning("llama-server --list-devices exceeded {TimeoutSeconds:0}s; treating the device list as unavailable.", timeout.TotalSeconds);
            return null;
        }
        finally
        {
            // Single reaping point: on success the child has already exited (no-op); on timeout or caller-cancel it is
            // killed (entire tree) so the probe never abandons a live process.
            TryKill(process);
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
        catch (Exception)
        {
            // Best-effort: the child may have exited between the check and the kill, or be unkillable; the probe result
            // stands either way.
        }
    }
}
