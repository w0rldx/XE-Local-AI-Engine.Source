namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Hub-backed <see cref="ICudaBuildEventPublisher" />. Broadcasts each CUDA build status event to all connected
///     operator clients under <see cref="CudaBuildHubEvents.StatusChanged" />, so the React client subscribes once and
///     appends streamed log lines / updates the phase without a follow-up REST poll. Replaces the no-op default the
///     provider registers. Payloads carry no app secrets (scrubbed-env build) and have cache-root/HOME prefixes redacted.
/// </summary>
internal sealed class CudaBuildEventPublisher(IHubContext<CudaBuildHub> hubContext) : ICudaBuildEventPublisher
{
    private readonly IHubContext<CudaBuildHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    public Task PublishStatusAsync(CudaBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _hubContext.Clients.All.SendAsync(CudaBuildHubEvents.StatusChanged, statusEvent, cancellationToken);
    }
}
