namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Hub-backed <see cref="IPreviewWorkflowEventPublisher" />. Delivers each event ONLY to the run's per-run group
///     (<see cref="PreviewWorkflowHub.RunGroup" />) under its <c>EventType</c> as the SignalR method name, so a
///     connection receives only the runs it subscribed to — the runId on every payload plus group scoping together
///     prevent cross-run contamination. Replaces the no-op default in the Client host.
///     Privacy (documented exception): these payloads carry the operator's own transient run output (the
///     Debug feature) over the localhost Operator hub; nothing is persisted, logged, or indexed.
/// </summary>
internal sealed class PreviewWorkflowEventPublisher(IHubContext<PreviewWorkflowHub> hubContext) : IPreviewWorkflowEventPublisher
{
    private readonly IHubContext<PreviewWorkflowHub> _hubContext =
        hubContext ?? throw new ArgumentNullException(nameof(hubContext));

    public Task PublishNodeAsync(PreviewWorkflowNodeHubEvent nodeEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nodeEvent);
        return _hubContext.Clients
                          .Group(PreviewWorkflowHub.RunGroup(nodeEvent.RunId))
                          .SendAsync(nodeEvent.EventType, nodeEvent, cancellationToken);
    }

    public Task PublishRunAsync(PreviewWorkflowRunHubEvent runEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        return _hubContext.Clients
                          .Group(PreviewWorkflowHub.RunGroup(runEvent.RunId))
                          .SendAsync(runEvent.EventType, runEvent, cancellationToken);
    }
}
