namespace XE_Local_AI_Engine.Client.Services.ExternalApps;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Pushes what just happened to an instance to whoever is watching. Two messages, both content-free: a status
///     ping carrying the sequence a subscriber re-fetches from, and pull progress, which carries no sequence and
///     writes no row.
///     <para>
///         A ping is published AFTER the commit that allocated its sequence, so a subscriber replaying from a
///         watermark can never be told about a row that is not yet readable.
///     </para>
/// </summary>
public interface IExternalAppEventPublisher
{
    /// <summary>One status change, identified by the sequence the store minted for it.</summary>
    Task PublishAsync(Guid instanceId,
        long sequence,
        ExternalAppInstanceEventKind kind,
        ExternalAppInstanceStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Image pull progress for one service. Best-effort by design: a dropped message degrades to a stale
    ///     progress bar, which is why it allocates no sequence and persists nothing.
    /// </summary>
    Task PublishPullProgressAsync(Guid instanceId,
        string service,
        int layerCount,
        int completedLayers,
        long bytes,
        CancellationToken cancellationToken = default);
}

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
