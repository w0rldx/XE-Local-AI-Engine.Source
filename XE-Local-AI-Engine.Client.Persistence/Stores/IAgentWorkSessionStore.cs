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

public sealed record AgentWorkSessionSnapshot
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Objective { get; init; }

    public required AgentWorkSessionKind Kind { get; init; }

    public required AgentWorkSessionStatus Status { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid? CurrentTaskId { get; init; }

    public required int StepCount { get; init; }

    public required Guid? LastCheckpointId { get; init; }

    public required long LastSequence { get; init; }

    public required int ConfigVersion { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }
}

public sealed class WorkSessionTaskSnapshot
{
    public required Guid Id { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid? ParentTaskId { get; init; }

    public required long Sequence { get; init; }

    public required string Title { get; init; }

    public required string? Detail { get; init; }

    public required AgentWorkSessionTaskStatus Status { get; init; }

    public required string? BlockedReason { get; init; }

    public required AgentWorkSessionTaskOrigin Origin { get; init; }

    public required int CreatedStep { get; init; }

    public required int UpdatedStep { get; init; }
}

public sealed class WorkSessionFindingSnapshot
{
    public required Guid Id { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid? TaskId { get; init; }

    public required long Sequence { get; init; }

    public required AgentWorkSessionFindingKind Kind { get; init; }

    public required string Text { get; init; }

    public required string? SourceRef { get; init; }

    public required int CreatedStep { get; init; }

    public required bool Superseded { get; init; }
}

public sealed class WorkSessionArtifactSnapshot
{
    public required Guid Id { get; init; }

    public required Guid SessionId { get; init; }

    public required long Sequence { get; init; }

    public required AgentWorkSessionArtifactKind Kind { get; init; }

    public required string Name { get; init; }

    public required string MediaType { get; init; }

    public required string ContentSha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required bool IsValid { get; init; }

    public required string ManagedReference { get; init; }

    public required int CreatedStep { get; init; }
}

public sealed record WorkSessionCheckpointSnapshot
{
    public required Guid Id { get; init; }

    public required Guid SessionId { get; init; }

    public required long Sequence { get; init; }

    public required int Step { get; init; }

    public required string? Summary { get; init; }

    public required string StateJson { get; init; }

    public required long CreatedAtUtc { get; init; }
}

public sealed class WorkSessionEventSnapshot
{
    public required Guid Id { get; init; }

    public required Guid SessionId { get; init; }

    public required long Sequence { get; init; }

    public required int Step { get; init; }

    public required string EventType { get; init; }

    public required string? DetailJson { get; init; }

    public required Guid? OperationId { get; init; }

    public required string? Outcome { get; init; }

    public required long OccurredAtUtc { get; init; }
}

public sealed class CreateWorkSessionCommand
{
    public required Guid SessionId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid AgentDefinitionId { get; init; }

    public required AgentWorkSessionKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Objective { get; init; }

    public int ConfigVersion { get; init; } = 1;
}

public sealed class UpdateWorkSessionCommand
{
    public required Guid SessionId { get; init; }

    public required long ExpectedVersion { get; init; }

    public string? Title { get; init; }

    public string? Objective { get; init; }

    public Guid? AgentDefinitionId { get; init; }
}

/// <summary>
///     A status move, optionally re-pointing the current task. A null <see cref="CurrentTaskId" /> leaves the current
///     task as it is; a terminal target clears it regardless.
/// </summary>
public sealed class TransitionWorkSessionStatusCommand
{
    public required Guid SessionId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required AgentWorkSessionStatus TargetStatus { get; init; }

    public Guid? CurrentTaskId { get; init; }

    public string? SanitizedReason { get; init; }
}

public sealed class WorkPlanTaskChange
{
    public required Guid TaskId { get; init; }

    public required WorkPlanTaskOperation Operation { get; init; }

    public Guid? ParentTaskId { get; init; }

    public string? Title { get; init; }

    public string? Detail { get; init; }

    public AgentWorkSessionTaskStatus? Status { get; init; }

    public string? BlockedReason { get; init; }
}

public sealed class ApplyWorkPlanCommand
{
    public required Guid SessionId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required AgentWorkSessionTaskOrigin Origin { get; init; }

    public required IReadOnlyList<WorkPlanTaskChange> Changes { get; init; }
}

public sealed class AppendWorkSessionFindingCommand
{
    public required Guid SessionId { get; init; }

    public required Guid FindingId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required AgentWorkSessionFindingKind Kind { get; init; }

    public required string Text { get; init; }

    public Guid? TaskId { get; init; }

    public string? SourceRef { get; init; }

    public Guid? SupersedesFindingId { get; init; }
}

public sealed class AppendWorkSessionArtifactCommand
{
    public required Guid SessionId { get; init; }

    public required Guid ArtifactId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required AgentWorkSessionArtifactKind Kind { get; init; }

    public required string Name { get; init; }

    public required string MediaType { get; init; }

    public required string ContentSha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required string ManagedReference { get; init; }
}

public sealed class AppendWorkSessionCheckpointCommand
{
    public required Guid SessionId { get; init; }

    public required Guid CheckpointId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required int Step { get; init; }

    public required string? Summary { get; init; }

    public required string StateJson { get; init; }
}

public sealed class AppendWorkSessionEventCommand
{
    public required Guid SessionId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required string EventType { get; init; }

    public Guid? OperationId { get; init; }

    public string? Outcome { get; init; }

    public string? DetailJson { get; init; }
}

/// <summary>
///     What one mutation committed: the watermark it allocated for its event, the session's step, and the session row's
///     post-commit version, status and current task.
///     <para>
///         <see cref="SupersededArtifactId" /> is set only by <see cref="IAgentWorkSessionStore.AppendArtifactAsync" />
///         when the write replaced an artifact of the same name. Its bytes are still on disk: the caller that owns the
///         blob store deletes them after the commit, because the schema project cannot reach the blob layer.
///     </para>
/// </summary>
public sealed class WorkSessionMutationResult
{
    public required Guid SessionId { get; init; }

    public required long Sequence { get; init; }

    public required int Step { get; init; }

    public required long Version { get; init; }

    public required AgentWorkSessionStatus Status { get; init; }

    public required Guid? CurrentTaskId { get; init; }

    public Guid? SupersededArtifactId { get; init; }
}

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
    ///     order is load-bearing: findings declare <c>Restrict</c> on their task, so a task cannot go first, and the
    ///     row count has to be observed rather than inferred from what the cascades would have removed.
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

public sealed class WorkSessionConcurrencyException : InvalidOperationException
{
    public WorkSessionConcurrencyException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

public sealed class WorkSessionInvalidTransitionException : InvalidOperationException
{
    public WorkSessionInvalidTransitionException(string message) : base(message)
    {
    }
}
