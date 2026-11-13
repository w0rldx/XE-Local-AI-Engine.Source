namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     The publisher a host without the graph-workflow hub resolves. Registered with <c>TryAddSingleton</c> so the
///     hub's own registration wins wherever it is present.
/// </summary>
internal sealed class NoOpGraphWorkflowEventPublisher : IGraphWorkflowEventPublisher
{
    public Task PublishAsync(Guid runId, long sequence, GraphWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
