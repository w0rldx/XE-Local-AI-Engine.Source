namespace XE_Local_AI_Engine.Providers.Capabilities.Implementation;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Capabilities.Contracts;

/// <summary>
///     Live <see cref="IProcessProbe" />: shells out to a lightweight, ubiquitous tool (e.g. <c>nvidia-smi</c>) and
///     captures its stdout under a wall-clock deadline. A missing tool / spawn failure degrades to <see langword="null" />
///     — never fatal — so the profiler can fall through to the next detection branch. A probe that overruns its timeout
///     is killed (process tree, so no orphaned <c>nvidia-smi</c> survives) and returns a
///     <see cref="ProcessProbeResult.TimedOut" /> result rather than hanging provisioning forever.
/// </summary>
internal sealed class ProcessProbe : IProcessProbe
{
    private readonly ILogger<ProcessProbe> _logger;

    public ProcessProbe(ILogger<ProcessProbe> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ProcessProbeResult?> RunAsync(string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        // Link the caller's token with an internal wall-clock deadline. Both cancel the pipe drains and the exit wait; the
        // finally-block tree-kill guarantees the child (and any grandchildren it spawned) never outlives this call.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(timeout);
        }

        Process? process = null;
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

            process = new Process
            {
                StartInfo = startInfo
            };
            if (!process.Start())
            {
                return null;
            }

            var standardOutput = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return new ProcessProbeResult(process.ExitCode, standardOutput);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation: tree-kill so a slow probe leaves no orphan, then surface the cancellation.
            TryKillTree(process, fileName);
            throw;
        }
        catch (OperationCanceledException)
        {
            // The internal wall-clock deadline fired (the caller's token did NOT): the tool is wedged. Kill the tree and
            // report a typed timeout so the profiler degrades to a cached/CPU-safe profile instead of hanging.
            TryKillTree(process, fileName);
            _logger.LogWarning("Hardware probe '{ProbeTool}' exceeded its {TimeoutSeconds:0.###}s deadline; killed the process tree and degrading.",
                fileName,
                timeout.TotalSeconds);
            return new ProcessProbeResult(ExitCode: -1, StandardOutput: string.Empty, TimedOut: true);
        }
        catch (Exception ex)
        {
            // Tool missing / not on PATH / permission denied — treat as "not detected", never fatal. Still tree-kill in
            // case the process started before the failure.
            TryKillTree(process, fileName);
            _logger.LogDebug(ex, "Hardware probe '{ProbeTool}' failed to run; treating as not detected.", fileName);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    // Best-effort tree-kill: the child may have exited between the HasExited check and the kill, or be unkillable; the
    // probe outcome (timeout / not-detected) stands either way. entireProcessTree reaps any helper the tool spawned.
    private void TryKillTree(Process? process, string fileName)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to kill the hardware probe '{ProbeTool}' process tree (it may have already exited).", fileName);
        }
    }
}
