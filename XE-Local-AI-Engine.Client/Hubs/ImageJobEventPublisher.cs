namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Hub-backed <see cref="IImageJobEventPublisher" />, replacing the no-op default in the Client host.
/// </summary>
/// <remarks>
///     Delivers each coarse status event ONLY to the job's per-job group (<see cref="ImageJobHub.JobGroup" />) under
///     <see cref="ImageJobHubEvents.StatusChanged" /> as the SignalR method name, so a connection receives only the
///     jobs it subscribed to. Payloads are coarse status only, already free of prompt/path/step detail — see
///     <see cref="ImageJobStatusHubEvent" />.
/// </remarks>
internal sealed class ImageJobEventPublisher : IImageJobEventPublisher
{
    private readonly IHubContext<ImageJobHub> _hubContext;

    public ImageJobEventPublisher(IHubContext<ImageJobHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(ImageJobStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _hubContext.Clients
                          .Group(ImageJobHub.JobGroup(statusEvent.JobId))
                          .SendAsync(ImageJobHubEvents.StatusChanged, statusEvent, cancellationToken);
    }
}
