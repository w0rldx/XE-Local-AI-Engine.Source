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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return service.ShutdownAsync(cancellationToken);
    }
}
