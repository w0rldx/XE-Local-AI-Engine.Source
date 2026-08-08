namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
/// Recovers the managed source-build state before the host becomes ready. Recovery failures are fatal because
/// continuing with an ambiguous adoption journal could expose an unverified runtime.
/// </summary>
internal sealed class StableDiffusionCppSourceBuildLifecycle(
    IStableDiffusionCppSourceBuildService service,
    ILogger<StableDiffusionCppSourceBuildLifecycle> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await service.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to recover the managed stable-diffusion.cpp source-build state.");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await service.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Same contract as CudaBuildStartupService.StopAsync: the host's shutdown token means "stop being
            // graceful", not "throw". ShutdownAsync awaits the start gate on this token, so an over-budget
            // shutdown throws here and Host.StopAsync rethrows the aggregate, killing the process with an
            // unhandled exception instead of exiting cleanly.
            logger.LogWarning("The managed stable-diffusion.cpp shutdown drain was cut short by the host shutdown "
                              + "budget; any in-flight build is abandoned and will be reconciled on the next start.");
        }
    }
}
