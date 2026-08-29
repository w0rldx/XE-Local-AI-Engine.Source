namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     What changed on a run, as the hub's subscribers see it. Four kinds, because they map onto exactly two refetches
///     plus one that also raises a notification — a fifth would be a kind no client reacts to differently.
/// </summary>
public enum DevWorkflowChangeKind
{
    /// <summary>Run status, counters, or an event row. The client re-reads the run.</summary>
    Run,

    /// <summary>A node run moved, or new node runs were materialized. The client re-reads the run, graph revision included.</summary>
    Node,

    /// <summary>A human is being asked to act, or has acted. The same refetch as <see cref="Run" />, plus the operator notification.</summary>
    Gate,

    /// <summary>An artifact was written, superseded or marked stale. The client re-reads the artifact feed.</summary>
    Artifact
}

/// <summary>
///     Announces a committed development-workflow change to whoever is watching the run. Called AFTER the commit that
///     allocated <c>sequence</c>, so a subscriber that replays from the watermark can never miss the row it names.
///     <para>
///         The payload is content-free by design: a dropped push degrades to a late read rather than to a wrong
///         render, because the database is the only replay authority.
///     </para>
/// </summary>
public interface IDevWorkflowEventPublisher
{
    Task PublishAsync(Guid runId, long sequence, DevWorkflowChangeKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
///     The publisher a host without the development-workflow hub resolves. Registered with <c>TryAddSingleton</c> so
///     the hub's own registration wins wherever it is present, exactly as the work-session pair does.
/// </summary>
internal sealed class NoOpDevWorkflowEventPublisher : IDevWorkflowEventPublisher
{
    public Task PublishAsync(Guid runId, long sequence, DevWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
