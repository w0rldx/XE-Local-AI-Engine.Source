namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Hub-backed <see cref="ISchedulerEventPublisher" />, replacing the no-op default in the Client host.
/// </summary>
/// <remarks>
///     Broadcasts each sanitized scheduler event to all connected clients under its <see cref="SchedulerHubEvents" />
///     method name, which each payload repeats as <c>eventType</c>, so the React client subscribes per event. Events are
///     already sanitized by the callers: no parameters, details, or stack traces.
/// </remarks>
internal sealed class SchedulerEventPublisher : ISchedulerEventPublisher
{
    private readonly IHubContext<SchedulerHub> _hubContext;

    public SchedulerEventPublisher(IHubContext<SchedulerHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public Task PublishRunAsync(SchedulerRunEvent runEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        var eventType = ToEventType(runEvent.Kind);
        return _hubContext.Clients.All.SendAsync(eventType, new SchedulerRunHubMessage
        {
            EventType = eventType,
            RunId = runEvent.RunId,
            ScheduledJobId = runEvent.ScheduledJobId,
            TemplateId = runEvent.TemplateId,
            Status = runEvent.Status,
            TriggeredBy = runEvent.TriggeredBy,
            ScheduledFireTimeUtc = runEvent.ScheduledFireTimeUtc,
            ActualFireTimeUtc = runEvent.ActualFireTimeUtc,
            CompletedAtUtc = runEvent.CompletedAtUtc,
            DurationMs = runEvent.DurationMs,
            Summary = runEvent.Summary,
            ErrorMessage = runEvent.ErrorMessage,
            OccurredAtUtc = runEvent.OccurredAtUtc
        }, cancellationToken);
    }

    public Task PublishRunProgressAsync(SchedulerRunProgressEvent progressEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progressEvent);
        return _hubContext.Clients.All.SendAsync(SchedulerHubEvents.RunProgress, new SchedulerRunProgressHubMessage
        {
            EventType = SchedulerHubEvents.RunProgress,
            RunId = progressEvent.RunId,
            ScheduledJobId = progressEvent.ScheduledJobId,
            Message = progressEvent.Message,
            Percent = progressEvent.Percent,
            OccurredAtUtc = progressEvent.OccurredAtUtc
        }, cancellationToken);
    }

    public Task PublishDefinitionAsync(SchedulerDefinitionEvent definitionEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitionEvent);
        return _hubContext.Clients.All.SendAsync(SchedulerHubEvents.JobDefinitionChanged, new SchedulerDefinitionHubMessage
        {
            EventType = SchedulerHubEvents.JobDefinitionChanged,
            ScheduledJobId = definitionEvent.ScheduledJobId,
            Action = definitionEvent.Action,
            OccurredAtUtc = definitionEvent.OccurredAtUtc
        }, cancellationToken);
    }

    private static string ToEventType(SchedulerRunEventKind kind)
    {
        return kind switch
        {
            SchedulerRunEventKind.Started => SchedulerHubEvents.RunStarted,
            SchedulerRunEventKind.Completed => SchedulerHubEvents.RunCompleted,
            SchedulerRunEventKind.Failed => SchedulerHubEvents.RunFailed,
            SchedulerRunEventKind.Cancelled => SchedulerHubEvents.RunCancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown scheduler run event kind.")
        };
    }
}
