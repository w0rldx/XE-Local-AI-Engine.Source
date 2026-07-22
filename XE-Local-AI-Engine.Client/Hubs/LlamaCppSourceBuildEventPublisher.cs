namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class LlamaCppSourceBuildEventPublisher(
    IHubContext<LlamaCppSourceBuildHub> sourceHubContext,
    IHubContext<CudaBuildHub> cudaHubContext) : ILlamaCppSourceBuildEventPublisher
{
    public async Task PublishStatusAsync(LlamaCppSourceBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        await sourceHubContext.Clients.All.SendAsync(LlamaCppSourceBuildHubEvents.StatusChanged, statusEvent, cancellationToken).ConfigureAwait(false);

        if (statusEvent.CurrentBuild.IsLegacyPinnedCuda())
        {
            await cudaHubContext.Clients.All.SendAsync(CudaBuildHubEvents.StatusChanged,
                new CudaBuildStatusHubEvent(statusEvent.Phase, statusEvent.AppendedLogLines, statusEvent.Terminal, statusEvent.SanitizedError),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
