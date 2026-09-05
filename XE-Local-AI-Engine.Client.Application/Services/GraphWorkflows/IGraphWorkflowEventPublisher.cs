namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     What changed on a run, as the hub's subscribers see it. Three kinds, because they map onto exactly two
///     refetches plus one that also raises an operator notification — a fourth would be a kind no client reacts to
///     differently. There is no <c>Artifact</c> member: a graph workflow writes documents onto its node runs, not into
///     a separate feed.
/// </summary>
public enum GraphWorkflowChangeKind
{
    /// <summary>Run status, or an event row. The client re-reads the run.</summary>
    Run,

    /// <summary>A node run moved. The client re-reads the run.</summary>
    Node,

    /// <summary>A human is being asked to act. The same refetch as <see cref="Run" />, plus the operator notification.</summary>
    Gate
}

/// <summary>
///     Announces a committed graph-workflow change to whoever is watching the run. Called AFTER the commit that
///     allocated <c>sequence</c>, so a subscriber that replays from the watermark can never miss the row it names.
///     <para>
///         The payload is content-free by design: a dropped push degrades to a late read rather than to a wrong
///         render, because the database is the only replay authority.
///     </para>
/// </summary>
public interface IGraphWorkflowEventPublisher
{
    Task PublishAsync(Guid runId, long sequence, GraphWorkflowChangeKind kind, CancellationToken cancellationToken = default);
}

/// <summary>
///     The publisher a host without the graph-workflow hub resolves. Registered with <c>TryAddSingleton</c> so the
///     hub's own registration wins wherever it is present, exactly as the development-workflow pair does.
/// </summary>
internal sealed class NoOpGraphWorkflowEventPublisher : IGraphWorkflowEventPublisher
{
    public Task PublishAsync(Guid runId, long sequence, GraphWorkflowChangeKind kind, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
