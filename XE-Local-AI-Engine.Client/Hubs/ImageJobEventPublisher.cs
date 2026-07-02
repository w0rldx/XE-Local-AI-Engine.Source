namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Hub-backed <see cref="IImageJobEventPublisher" />. Delivers each coarse status event ONLY to the job's per-job
///     group (<see cref="ImageJobHub.JobGroup" />) under <see cref="ImageJobHubEvents.StatusChanged" /> as the SignalR
///     method name, so a connection receives only the jobs it subscribed to. Replaces the no-op default in the Client
///     host. Payloads are coarse status only (already free of prompt/path/step detail) — see
///     <see cref="ImageJobStatusHubEvent" />.
/// </summary>
internal sealed class ImageJobEventPublisher(IHubContext<ImageJobHub> hubContext) : IImageJobEventPublisher
{
    private readonly IHubContext<ImageJobHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    public Task PublishStatusAsync(ImageJobStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _hubContext.Clients
                          .Group(ImageJobHub.JobGroup(statusEvent.JobId))
                          .SendAsync(ImageJobHubEvents.StatusChanged, statusEvent, cancellationToken);
    }
}
