namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Hub-backed <see cref="ISchedulerEventPublisher" />. Broadcasts each sanitized scheduler event to all connected
///     clients under its <c>EventType</c> as the SignalR method name, so the React client subscribes per event. Replaces
///     the no-op default in the Client host. Payloads are already sanitized by the callers (no parameters, details, or
///     stack traces).
/// </summary>
internal sealed class SchedulerEventPublisher(IHubContext<SchedulerHub> hubContext) : ISchedulerEventPublisher
{
    private readonly IHubContext<SchedulerHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    public Task PublishRunAsync(SchedulerRunHubEvent runEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        return _hubContext.Clients.All.SendAsync(runEvent.EventType, runEvent, cancellationToken);
    }

    public Task PublishRunProgressAsync(SchedulerRunProgressHubEvent progressEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        return _hubContext.Clients.All.SendAsync(progressEvent.EventType, progressEvent, cancellationToken);
    }

    public Task PublishDefinitionAsync(SchedulerDefinitionHubEvent definitionEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionEvent);
        return _hubContext.Clients.All.SendAsync(definitionEvent.EventType, definitionEvent, cancellationToken);
    }
}
