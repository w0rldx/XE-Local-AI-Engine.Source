namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

/// <summary>
///     No-op <see cref="IPreviewWorkflowEventPublisher" />. Registered as the default so the execution service resolves
///     a publisher even when no SignalR hub is wired (Application-only and test hosts). The Client host registers a
///     hub-backed publisher that supersedes this one.
/// </summary>
internal sealed class NullPreviewWorkflowEventPublisher : IPreviewWorkflowEventPublisher
{
    public Task PublishNodeAsync(PreviewWorkflowNodeHubEvent nodeEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishRunAsync(PreviewWorkflowRunHubEvent runEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
