namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

/// <summary>
///     No-op <see cref="ISchedulerEventPublisher" />. Registered as the default in <c>AddNodeScheduler</c> so the
///     dispatcher and management service resolve a publisher even when no SignalR hub is wired (Application-only and test
///     hosts). The Client host registers a hub-backed publisher that supersedes this one.
/// </summary>
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
