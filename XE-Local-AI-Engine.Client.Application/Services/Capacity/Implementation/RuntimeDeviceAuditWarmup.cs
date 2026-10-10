namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Pays the first <c>--list-devices</c> probe of a freshly installed llama.cpp runtime before the first chat needs it.
/// </summary>
/// <remarks>
///     On a cold Windows CUDA driver that probe can run to its full timeout, and the probe remembers a failure, so warming right after
///     an install keeps the chat path from waiting on it. Every path that installs a runtime calls this one helper, so no install path
///     can skip the warm. A warm failure is logged at Debug and never thrown: the audit re-probes on its next demand.
/// </remarks>
internal static class RuntimeDeviceAuditWarmup
{
    public static async Task WarmAsync(IRuntimeDeviceAudit deviceAudit, GpuVariant variant, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceAudit);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await deviceAudit.GetAuditAsync(forceRefresh: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping: nothing to warm.
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Warming the runtime device audit after acquiring {Variant} failed.", variant);
        }
    }
}
