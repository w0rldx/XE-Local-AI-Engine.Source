namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The publisher used when no hub is mapped — in tests, and in a host that composes the services without the API
///     surface. Registered with <c>TryAddSingleton</c> so the hub-backed implementation wins wherever it is present.
/// </summary>
public sealed class NoOpExternalAppEventPublisher : IExternalAppEventPublisher
{
    public Task PublishAsync(Guid instanceId,
        long sequence,
        ExternalAppInstanceEventKind kind,
        ExternalAppInstanceStatus status,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task PublishPullProgressAsync(Guid instanceId,
        string service,
        int layerCount,
        int completedLayers,
        long bytes,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
