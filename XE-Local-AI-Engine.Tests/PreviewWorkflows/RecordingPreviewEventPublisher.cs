namespace XE_Local_AI_Engine.Tests.PreviewWorkflows;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>Records every published preview event so tests can assert kinds, runIds, and ordering.</summary>
internal sealed class RecordingPreviewEventPublisher : IPreviewWorkflowEventPublisher
{
    public ConcurrentQueue<PreviewWorkflowNodeHubEvent> NodeEvents { get; } = new();

    public ConcurrentQueue<PreviewWorkflowRunHubEvent> RunEvents { get; } = new();

    public Task PublishNodeAsync(PreviewWorkflowNodeHubEvent nodeEvent, CancellationToken cancellationToken = default)
    {
        NodeEvents.Enqueue(nodeEvent);
        return Task.CompletedTask;
    }

    public Task PublishRunAsync(PreviewWorkflowRunHubEvent runEvent, CancellationToken cancellationToken = default)
    {
        RunEvents.Enqueue(runEvent);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> RunEventTypes => [.. RunEvents.Select(e => e.EventType)];

    public bool HasRunEvent(string eventType) => RunEvents.Any(e => string.Equals(e.EventType, eventType, StringComparison.Ordinal));
}
