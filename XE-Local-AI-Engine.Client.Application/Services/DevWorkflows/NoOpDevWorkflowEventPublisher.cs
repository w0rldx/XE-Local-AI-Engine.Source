namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     The publisher a host without the development-workflow hub resolves. Registered with <c>TryAddSingleton</c> so
///     the hub's own registration wins wherever it is present, exactly as the work-session pair does.
/// </summary>
internal sealed class NoOpDevWorkflowEventPublisher : IDevWorkflowEventPublisher
{
    public Task PublishAsync(Guid runId, long sequence, DevWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
