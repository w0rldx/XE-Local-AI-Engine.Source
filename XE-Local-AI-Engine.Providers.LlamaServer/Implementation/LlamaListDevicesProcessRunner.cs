namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

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
        var result = await new LlamaCommandProcessRunner(logger)
                           .RunAsync(executablePath, ["--list-devices"], timeout, ct)
                           .ConfigureAwait(false);
        return result?.CombinedOutput;
    }
}
