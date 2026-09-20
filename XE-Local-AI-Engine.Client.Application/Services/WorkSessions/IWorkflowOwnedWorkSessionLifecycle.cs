namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Everything a development-workflow run does to the work session one of its agent node-runs owns: create it, read
///     what it is doing, and drive its lifecycle.
/// </summary>
/// <remarks>
///     <see cref="IWorkSessionService" />'s five verbs REFUSE a <c>Workflow</c>-kind session and each verb here
///     refuses one that is not, both in the service rather than the UI so a headless caller is covered. The split
///     removes an ambiguity the node-run's poll cannot resolve: a step-budget pause must auto-resume, an operator's
///     pause means the opposite, and a reason string is a guess — so the operator pauses the RUN instead. It is also
///     the runtime's one agent seam, deliberately: two overlapping fakes could disagree about a session.
/// </remarks>
internal interface IWorkflowOwnedWorkSessionLifecycle
{
    /// <summary>
    ///     Whether the node could admit another session right now. A hint read BEFORE a node-run is moved to
    ///     <c>Running</c>, so a full node leaves the row <c>Queued</c> with a reason rather than claiming it is working.
    /// </summary>
    bool HasCapacity { get; }

    /// <summary>
    ///     Creates a session of the workflow kind, with the conversation and the agent checks the ordinary create does.
    /// </summary>
    /// <remarks>
    ///     <paramref name="runtime" /> pins what THIS session runs on over the bound agent's own pins and is checked by
    ///     the same tool gate, so a node naming a model this node cannot run refuses here.
    /// </remarks>
    Task<WorkSessionDetail> CreateAsync(string title,
        string objective,
        Guid agentDefinitionId,
        WorkSessionRuntimeOverride? runtime = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     What the session is doing, which is the whole of the node-run's poll.
    /// </summary>
    /// <remarks>
    ///     Deliberately NOT ownership-checked: a read cannot put a session in the wrong hands, and refusing one would
    ///     make the poll handle an exception for a session the run already owns.
    /// </remarks>
    Task<WorkSessionDetail> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Starts the session's step loop.
    /// </summary>
    /// <remarks>
    ///     <paramref name="runtime" /> is re-supplied on every entry rather than stored: the owning run's graph
    ///     snapshot is the durable copy, so a restart that leaves a session <c>Interrupted</c> gets the node's
    ///     authored model and effort back from the run that resumes it.
    /// </remarks>
    Task<WorkSessionDetail> StartAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime = null, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="StartAsync" />
    Task<WorkSessionDetail> ResumeAsync(Guid sessionId, WorkSessionRuntimeOverride? runtime = null, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
