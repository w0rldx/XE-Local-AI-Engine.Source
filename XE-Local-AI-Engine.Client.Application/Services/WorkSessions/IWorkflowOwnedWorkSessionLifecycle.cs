namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Everything a development-workflow run does to the work session one of its agent node-runs owns: create it, read
///     what it is doing, and drive its lifecycle.
///     <para>
///         <see cref="IWorkSessionService" />'s own five verbs REFUSE a session whose kind is
///         <c>AgentWorkSessionKind.Workflow</c>, and this is the surface the owning run drives it through instead. The
///         split exists because the node-run's poll cannot otherwise read a <c>Paused</c> session: pausing on the step
///         budget is routine and must auto-resume, while an operator pausing the same session through the ordinary Work
///         Sessions page means the opposite — and inferring which from a reason string would be a guess. Removing the
///         second case removes the ambiguity, and the operator's control becomes pausing the RUN.
///     </para>
///     <para>
///         Enforced in the service rather than in the UI, so a headless caller is covered too. Each verb here refuses a
///         session that is <em>not</em> workflow-owned, for the same reason in reverse.
///     </para>
///     <para>
///         This is ALSO the runtime's agent seam — the one interface a test fakes to exercise the graph without a model,
///         and deliberately the only one: a separate create/poll seam beside this one would give the agent lane two
///         overlapping fakes that could disagree about what a session is doing.
///     </para>
/// </summary>
internal interface IWorkflowOwnedWorkSessionLifecycle
{
    /// <summary>
    ///     Whether the node could admit another session right now. A hint read BEFORE a node-run is moved to
    ///     <c>Running</c>, so a full node leaves the row <c>Queued</c> with a reason rather than claiming it is working.
    /// </summary>
    bool HasCapacity { get; }

    /// <summary>Creates a session of the workflow kind, with the conversation and the agent checks the ordinary create does.</summary>
    Task<WorkSessionDetail> CreateAsync(string title, string objective, Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     What the session is doing, which is the whole of the node-run's poll. Deliberately NOT ownership-checked: a
    ///     read cannot put a session in the wrong hands, and refusing one would make the poll handle an exception for a
    ///     session the run already owns.
    /// </summary>
    Task<WorkSessionDetail> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> StartAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
