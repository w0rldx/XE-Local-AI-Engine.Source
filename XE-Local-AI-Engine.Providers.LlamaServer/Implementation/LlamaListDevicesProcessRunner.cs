namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Logging;

/// <summary>
///     Shared runner for a short-lived <c>llama-server --list-devices</c> probe, so the process launch, pipe draining
///     and bounded wait live here once.
/// </summary>
/// <remarks>
///     Both <see cref="LlamaListDevicesProcessVramBudgetProbe" /> and <see cref="LlamaDeviceInventoryProbe" /> ask
///     llama.cpp the same question — what devices does THIS binary enumerate? Unlike the supervised server this is a
///     run-to-exit probe, so a plain <see cref="Process" /> with both pipes drained and a bounded wait suffices, with
///     no Job Object or setsid containment. The working directory is co-located with the binary so its bundled runtime
///     libraries (cudart, vulkan, ggml) resolve, mirroring the launcher.
/// </remarks>
internal static class LlamaListDevicesProcessRunner
{
    /// <summary>
    ///     Runs the device probe to exit, draining stdout AND stderr, bounded by <paramref name="timeout" />, and
    ///     returns the combined output or <see langword="null" /> on a failed start or a timeout overrun.
    /// </summary>
    /// <remarks>
    ///     llama.cpp writes the device table to one pipe and its backend banner to the other. Genuine caller
    ///     cancellation (<paramref name="ct" />) is honoured and rethrown; a timeout is not, degrading to null.
    /// </remarks>
    internal static async Task<string?> RunAsync(string executablePath, TimeSpan timeout, ILogger logger, CancellationToken ct)
    {
        var result = await new LlamaCommandProcessRunner(logger)
                           .RunAsync(executablePath, ["--list-devices"], timeout, ct)
                           .ConfigureAwait(false);
        return result?.CombinedOutput;
    }
}
