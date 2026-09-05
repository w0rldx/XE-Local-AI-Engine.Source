namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The sentinel <see cref="Any" /> version. Two writers touch one session per step by design — the supervisor moves
///     the status while tool handlers write tasks, findings and artifacts from inside the invocation loop — so a
///     supervisor-owned status transition or step advance, which has no lost update to protect against, passes
///     <see cref="Any" /> and never loses the race to a content write.
/// </summary>
public static class WorkSessionVersions
{
    public const long Any = -1;
}

public enum WorkPlanTaskOperation
{
    Add,
    Update,
    Complete,
    Drop
}

public sealed record AgentWorkSessionSnapshot(
    Guid Id,
    string Title,
    string Objective,
    AgentWorkSessionKind Kind,
    AgentWorkSessionStatus Status,
    Guid AgentDefinitionId,
    Guid ConversationId,
    Guid? CurrentTaskId,
    int StepCount,
    Guid? LastCheckpointId,
    long LastSequence,
    int ConfigVersion,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version);

public sealed record WorkSessionTaskSnapshot(
    Guid Id,
    Guid SessionId,
    Guid? ParentTaskId,
    long Sequence,
    string Title,
    string? Detail,
    AgentWorkSessionTaskStatus Status,
    string? BlockedReason,
    AgentWorkSessionTaskOrigin Origin,
    int CreatedStep,
    int UpdatedStep);

public sealed record WorkSessionFindingSnapshot(
    Guid Id,
    Guid SessionId,
    Guid? TaskId,
    long Sequence,
    AgentWorkSessionFindingKind Kind,
    string Text,
    string? SourceRef,
    int CreatedStep,
    bool Superseded);

public sealed record WorkSessionArtifactSnapshot(
    Guid Id,
    Guid SessionId,
    long Sequence,
    AgentWorkSessionArtifactKind Kind,
    string Name,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    bool IsValid,
    string ManagedReference,
    int CreatedStep);

public sealed record WorkSessionCheckpointSnapshot(
    Guid Id,
    Guid SessionId,
    long Sequence,
    int Step,
    string? Summary,
    string StateJson,
    long CreatedAtUtc);

public sealed record WorkSessionEventSnapshot(
    Guid Id,
    Guid SessionId,
    long Sequence,
    int Step,
    string EventType,
    string? DetailJson,
    Guid? OperationId,
    string? Outcome,
    long OccurredAtUtc);

public sealed record CreateWorkSessionCommand(
    Guid SessionId,
    Guid ConversationId,
    Guid AgentDefinitionId,
    AgentWorkSessionKind Kind,
    string Title,
    string Objective,
    int ConfigVersion = 1);

public sealed record UpdateWorkSessionCommand(
    Guid SessionId,
    long ExpectedVersion,
    string? Title = null,
    string? Objective = null,
    Guid? AgentDefinitionId = null);

/// <summary>
///     A status move, optionally re-pointing the current task. A null <see cref="CurrentTaskId" /> leaves the current
///     task as it is; a terminal target clears it regardless.
/// </summary>
public sealed record TransitionWorkSessionStatusCommand(
    Guid SessionId,
    long ExpectedVersion,
    AgentWorkSessionStatus TargetStatus,
    Guid? CurrentTaskId = null,
    string? SanitizedReason = null);

public sealed record WorkPlanTaskChange(
    Guid TaskId,
    WorkPlanTaskOperation Operation,
    Guid? ParentTaskId = null,
    string? Title = null,
    string? Detail = null,
    AgentWorkSessionTaskStatus? Status = null,
    string? BlockedReason = null);

public sealed record ApplyWorkPlanCommand(
    Guid SessionId,
    long ExpectedVersion,
    Guid OperationId,
    AgentWorkSessionTaskOrigin Origin,
    IReadOnlyList<WorkPlanTaskChange> Changes);

public sealed record AppendWorkSessionFindingCommand(
    Guid SessionId,
    Guid FindingId,
    long ExpectedVersion,
    Guid OperationId,
    AgentWorkSessionFindingKind Kind,
    string Text,
    Guid? TaskId = null,
    string? SourceRef = null,
    Guid? SupersedesFindingId = null);

public sealed record AppendWorkSessionArtifactCommand(
    Guid SessionId,
    Guid ArtifactId,
    long ExpectedVersion,
    Guid OperationId,
    AgentWorkSessionArtifactKind Kind,
    string Name,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    string ManagedReference);

public sealed record AppendWorkSessionCheckpointCommand(
    Guid SessionId,
    Guid CheckpointId,
    long ExpectedVersion,
    Guid OperationId,
    int Step,
    string? Summary,
    string StateJson);

public sealed record AppendWorkSessionEventCommand(
    Guid SessionId,
    long ExpectedVersion,
    string EventType,
    Guid? OperationId = null,
    string? Outcome = null,
    string? DetailJson = null);

/// <summary>
///     What one mutation committed: the watermark it allocated for its event, the session's step, and the session row's
///     post-commit version, status and current task.
///     <para>
///         <see cref="SupersededArtifactId" /> is set only by <see cref="IAgentWorkSessionStore.AppendArtifactAsync" />
///         when the write replaced an artifact of the same name. Its bytes are still on disk: the caller that owns the
///         blob store deletes them after the commit, because the schema project cannot reach the blob layer.
///     </para>
/// </summary>
public sealed record WorkSessionMutationResult(
    Guid SessionId,
    long Sequence,
    int Step,
    long Version,
    AgentWorkSessionStatus Status,
    Guid? CurrentTaskId,
    Guid? SupersededArtifactId = null);

/// <summary>
///     The durable substrate for agent work sessions: one monotonic sequence per session, an append-only event log, and
///     optimistic concurrency on the session row.
///     <para>
///         Every mutation runs in one transaction that loads the session row, checks <c>ExpectedVersion</c> (unless it
///         is <see cref="WorkSessionVersions.Any" />), allocates sequence values from the session's counter, appends one
///         event, and bumps the version. A non-null operation id resolves query-first: an operation already recorded
///         returns without writing, so a replayed step cannot double-append.
///     </para>
/// </summary>
public interface IAgentWorkSessionStore
{
    Task<AgentWorkSessionSnapshot> CreateAsync(CreateWorkSessionCommand command, CancellationToken cancellationToken = default);

    Task<AgentWorkSessionSnapshot> UpdateAsync(UpdateWorkSessionCommand command, CancellationToken cancellationToken = default);

    Task<AgentWorkSessionSnapshot> TransitionStatusAsync(TransitionWorkSessionStatusCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the session and every child row in explicit dependency order, and answers how many rows went. The
    ///     node connection runs without <c>PRAGMA foreign_keys</c>, so the declared cascades never fire and the order
    ///     here is the only thing that keeps the delete complete.
    /// </summary>
    Task<int> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentWorkSessionSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    Task<AgentWorkSessionSnapshot> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<AgentWorkSessionSnapshot?> FindByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionTaskSnapshot>> ListTasksAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionFindingSnapshot>> ListFindingsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionArtifactSnapshot>> ListArtifactsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionCheckpointSnapshot>> ListCheckpointsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkSessionEventSnapshot>> ListEventsAsync(Guid sessionId, long sinceSequence = 0, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The newest event of one type, or <see langword="null" /> when the session recorded none. One ordered read
    ///     instead of the whole log: a caller asking "what did this session last declare" used to materialize and
    ///     decrypt every event ever written to it just to keep the final row.
    /// </summary>
    Task<WorkSessionEventSnapshot?> FindLatestEventAsync(Guid sessionId, string eventType, CancellationToken cancellationToken = default);

    Task<WorkSessionArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);

    Task<WorkSessionCheckpointSnapshot?> GetLatestCheckpointAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<WorkSessionMutationResult> ApplyPlanAsync(ApplyWorkPlanCommand command, CancellationToken cancellationToken = default);

    Task<WorkSessionMutationResult> AppendFindingAsync(AppendWorkSessionFindingCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records an artifact, replacing any artifact already carrying the same name on the session. The replaced row
    ///     goes in the same transaction and its managed reference is recorded on the event detail; deleting its bytes is
    ///     the caller's job (see <see cref="WorkSessionMutationResult.SupersededArtifactId" />).
    /// </summary>
    Task<WorkSessionMutationResult> AppendArtifactAsync(AppendWorkSessionArtifactCommand command, CancellationToken cancellationToken = default);

    Task<WorkSessionMutationResult> AppendCheckpointAsync(AppendWorkSessionCheckpointCommand command, CancellationToken cancellationToken = default);

    Task<WorkSessionMutationResult> AppendEventAsync(AppendWorkSessionEventCommand command, CancellationToken cancellationToken = default);

    Task<WorkSessionMutationResult> AdvanceStepAsync(Guid sessionId, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Collapses every session the host left mid-flight — <c>Running</c>, <c>WaitingForApproval</c> and
    ///     <c>WaitingForInput</c> — to <c>Interrupted</c>, and answers how many moved. Idempotent by construction: a
    ///     second pass finds none of those states and returns zero.
    /// </summary>
    Task<int> ReconcileRunningSessionsAsync(string sanitizedReason, CancellationToken cancellationToken = default);
}

public sealed class WorkSessionConcurrencyException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

public sealed class WorkSessionInvalidTransitionException(string message) : InvalidOperationException(message);
