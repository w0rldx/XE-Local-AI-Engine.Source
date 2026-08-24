namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The whole work-session surface the REST layer sits on: CRUD, the lifecycle verbs, the five sequence-filtered
///     feeds, artifact content, and the user follow-up.
///     <para>
///         Three exception types cross this boundary and nothing else: <see cref="KeyNotFoundException" /> for an
///         unknown session, task, finding or artifact; <c>WorkSessionInvalidTransitionException</c> (the store's, in
///         Persistence) for a lifecycle call the session's status forbids; and
///         <see cref="WorkSessionValidationException" /> for a bad input. Every reader throws rather than returning a
///         nullable, so the endpoint layer has one error path instead of two.
///     </para>
/// </summary>
public interface IWorkSessionService
{
    Task<IReadOnlyList<WorkSessionSummary>> ListAsync(CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates a session and the conversation it owns. The agent must exist and its effective model must be
    ///     tool-capable — a session whose model cannot call a tool would run its whole step budget writing nothing.
    /// </summary>
    Task<WorkSessionDetail> CreateAsync(CreateWorkSessionRequestModel model, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies the non-null members. <c>Title</c> is editable in any state; <c>Objective</c> and
    ///     <c>AgentDefinitionId</c> only in <c>Draft</c>, <c>Paused</c> or <c>Interrupted</c>, because either would swap
    ///     the state block or the tool set under a running turn.
    /// </summary>
    Task<WorkSessionDetail> UpdateAsync(Guid sessionId, UpdateWorkSessionRequestModel model, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the session, its owned conversation and its artifact bytes. Refused while a step is in flight —
    ///     cancel first, rather than racing teardown against the conversation the step is writing to.
    /// </summary>
    Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> StartAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> PauseAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> ResumeAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionDetail> CancelAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Persists <paramref name="text" /> as an ordinary user message on the owned conversation, so the next step's
    ///     history carries it. A <c>Paused</c> or <c>Interrupted</c> session is asked for a step once the row commits; a
    ///     parked one is not, because its live step already owns the node's invocation slot and its prompt is answered
    ///     through the chat card. Returns the persisted message id.
    /// </summary>
    Task<Guid> PostFollowUpAsync(Guid sessionId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The five feeds all filter on <c>sinceSequence</c> (pass 0 for everything). Tasks and artifacts are re-stamped
    ///     on every mutation, so a since-list replays updates as well as inserts.
    /// </summary>
    Task<IReadOnlyList<WorkSessionTaskDto>> ListTasksAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionFindingDto>> ListFindingsAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionArtifactDto>> ListArtifactsAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionCheckpointDto>> ListCheckpointsAsync(Guid sessionId, long sinceSequence, CancellationToken cancellationToken = default);

    /// <summary>Events, oldest first. <paramref name="limit" /> is clamped to 500.</summary>
    Task<IReadOnlyList<WorkSessionEventDto>> ListEventsAsync(Guid sessionId, long sinceSequence, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads one artifact's bytes. Throws <see cref="KeyNotFoundException" /> for an unknown artifact, one that
    ///     belongs to a different session, or one whose bytes no longer verify — a caller must not be handed content the
    ///     node cannot vouch for.
    /// </summary>
    Task<WorkSessionArtifactContent> ReadArtifactContentAsync(Guid sessionId, Guid artifactId, CancellationToken cancellationToken = default);
}
