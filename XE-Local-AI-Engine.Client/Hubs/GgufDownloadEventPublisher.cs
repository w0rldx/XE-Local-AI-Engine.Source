namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Hub-backed <see cref="IGgufDownloadEventPublisher" />. Broadcasts each sanitized download status to all connected
///     clients under <see cref="GgufDownloadHubEvents.StatusChanged" /> as the SignalR method name, so the React client
///     subscribes once and reconciles each push by model name. Replaces the no-op default in the Client host. Payloads
///     are already sanitized by the coordinator at the broadcast boundary (no path, URL, or token).
/// </summary>
internal sealed class GgufDownloadEventPublisher : IGgufDownloadEventPublisher
{
    private readonly IHubContext<GgufDownloadHub> _hubContext;

    public GgufDownloadEventPublisher(IHubContext<GgufDownloadHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(GgufDownloadStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _hubContext.Clients.All.SendAsync(GgufDownloadHubEvents.StatusChanged, statusEvent, cancellationToken);
    }
}
