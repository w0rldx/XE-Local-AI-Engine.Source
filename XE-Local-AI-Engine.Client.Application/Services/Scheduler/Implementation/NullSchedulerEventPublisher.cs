namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

/// <summary>
///     No-op <see cref="ISchedulerEventPublisher" />, the default registered in <c>AddNodeScheduler</c>.
/// </summary>
/// <remarks>
///     It lets the dispatcher and management service resolve a publisher even when no SignalR hub is wired, as in
///     Application-only and test hosts; the Client host registers a hub-backed publisher that supersedes it.
/// </remarks>
internal sealed class NullSchedulerEventPublisher : ISchedulerEventPublisher
{
    public Task PublishRunAsync(SchedulerRunHubEvent runEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishRunProgressAsync(SchedulerRunProgressHubEvent progressEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishDefinitionAsync(SchedulerDefinitionHubEvent definitionEvent, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
