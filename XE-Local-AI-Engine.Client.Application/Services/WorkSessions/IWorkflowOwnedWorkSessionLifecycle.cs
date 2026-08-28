namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The lifecycle verbs for a session a development-workflow run owns.
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
/// </summary>
internal interface IWorkflowOwnedWorkSessionLifecycle
{
    Task<WorkSessionDetail> StartAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
