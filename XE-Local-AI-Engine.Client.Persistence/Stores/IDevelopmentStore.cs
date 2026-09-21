namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public static class DevelopmentOperationPhases
{
    public const string Completed = "Completed";
    public const string ApplyStarted = "ApplyStarted";
    public const string ApplyCompleted = "ApplyCompleted";
    public const string ApplyBlocked = "ApplyBlocked";
}

public sealed record DevelopmentCreateProjectCommand
{
    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid OperationId { get; init; }

    public required string Objective { get; init; }

    public required Guid SelectedFolderId { get; init; }

    public required string RepositoryIdentityHash { get; init; }

    public required string BaseBranch { get; init; }

    public required string Title { get; init; }

    public required string Requirements { get; init; }

    public required string AcceptanceCriteriaJson { get; init; }

    public DevelopmentEgressPolicy EgressPolicy { get; init; }

    public string? CoderModelId { get; init; }

    public string? ReviewerModelId { get; init; }

    public int MaxReviewRounds { get; init; } = 3;

    public int ConfigurationVersion { get; init; } = 1;

    public bool TrustedRepositoryAcknowledged { get; init; }

    public int? TrustedRepositoryPolicyVersion { get; init; }

    public long? TrustedRepositoryAcknowledgedAtUtc { get; init; }

    public int? MaxTokens { get; init; }

    public int? MaxDurationSeconds { get; init; }

    public string? CommandProfileJson { get; init; }
}

/// <summary>
///     One more task on a project that already exists — the task-shaped half of
///     <see cref="DevelopmentCreateProjectCommand" />, with everything the project owns (repository, trust
///     acknowledgement, models, egress policy, command profile) inherited by living in it.
/// </summary>
public sealed class DevelopmentCreateTaskCommand
{
    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid OperationId { get; init; }

    public required string Title { get; init; }

    public required string Requirements { get; init; }

    public required string AcceptanceCriteriaJson { get; init; }

    public int MaxReviewRounds { get; init; } = 3;
}

public sealed class DevelopmentStartAttemptCommand
{
    public required Guid TaskId { get; init; }

    public required Guid AttemptId { get; init; }

    public required Guid OperationId { get; init; }

    public required DevelopmentAttemptRole Role { get; init; }

    public required string ModelId { get; init; }

    public required string Provider { get; init; }

    public required long ExpectedTaskVersion { get; init; }

    public Guid? PredecessorAttemptId { get; init; }
}

public sealed class DevelopmentTerminalizeAttemptCommand
{
    public required Guid AttemptId { get; init; }

    public required Guid OperationId { get; init; }

    public required DevelopmentAttemptStatus Status { get; init; }

    public required long ExpectedAttemptVersion { get; init; }

    public string? TerminalReason { get; init; }

    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }
}

public sealed class DevelopmentTransitionTaskCommand
{
    public required Guid TaskId { get; init; }

    public required Guid OperationId { get; init; }

    public required DevelopmentTaskStatus TargetStatus { get; init; }

    public required long ExpectedTaskVersion { get; init; }

    public string? Reason { get; init; }

    public string? ApprovedSubjectHash { get; init; }

    /// <summary>
    ///     That a PERSON wrote <see cref="Reason" />, rather than a reviewer, a gate or a workflow's own fix loop. It
    ///     is what lets the prompts rank it: an operator's sentence amends the task's immutable requirements, and a
    ///     reviewer's does not.
    /// </summary>
    public bool OperatorDirected { get; init; }

    /// <summary>
    ///     Raises the task's <c>MaxReviewRounds</c> by one, and is the ONLY thing that opens the single edge out of
    ///     <c>Blocked</c>.
    /// </summary>
    /// <remarks>
    ///     A person retrying a workflow node stopped at "all N rounds used" is buying the task the round it needs, the
    ///     same way that click already buys the node one more attempt; a task let out of Blocked without one would
    ///     spend a whole coder round to be stood down again on the same sentence. CALLER-TRUSTED: the store verifies
    ///     no operator did anything, so only an operator-DECISION path may set it — today
    ///     <c>DevWorkflowDevTaskExecutor.CarryOperatorRetryAsync</c>, gated on <c>IsOperatorRetry</c>.
    /// </remarks>
    public bool WidenReviewRounds { get; init; }
}

public sealed class DevelopmentStartValidationCommand
{
    public required Guid TaskId { get; init; }

    public required Guid OperationId { get; init; }

    public required long ExpectedTaskVersion { get; init; }
}

public sealed class DevelopmentInvalidateEvidenceCommand
{
    public required Guid TaskId { get; init; }

    public required Guid OperationId { get; init; }

    public required long ExpectedTaskVersion { get; init; }

    public required string SanitizedReason { get; init; }
}

public sealed class DevelopmentFinalizeValidationCommand
{
    public required DevelopmentAttachArtifactCommand Artifact { get; init; }

    public required Guid OperationId { get; init; }

    public required long ExpectedTaskVersion { get; init; }

    public required DevelopmentTaskStatus TargetStatus { get; init; }

    public string? SanitizedReason { get; init; }
}

public sealed class DevelopmentFinalizeReviewCommand
{
    public required DevelopmentAttachArtifactCommand Artifact { get; init; }

    public required Guid OperationId { get; init; }

    public required long ExpectedTaskVersion { get; init; }

    public required long ExpectedAttemptVersion { get; init; }

    public required DevelopmentTaskStatus TargetStatus { get; init; }

    public required string? ApprovedSubjectHash { get; init; }

    public required string? SanitizedReason { get; init; }

    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }
}

public sealed class DevelopmentAttachArtifactCommand
{
    public required Guid ArtifactId { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid? AttemptId { get; init; }

    public required Guid OperationId { get; init; }

    public required DevelopmentArtifactKind Kind { get; init; }

    public required int SchemaVersion { get; init; }

    public required string ContentHash { get; init; }

    public required long ByteCount { get; init; }

    public ReadOnlyMemory<byte>? ContentJson { get; init; }

    public string? ManagedReference { get; init; }

    public string? BaseCommit { get; init; }

    public string? SubjectHash { get; init; }

    public string? ChangedFilesManifestHash { get; init; }

    public ReadOnlyMemory<byte>? InputArtifactIdsJson { get; init; }

    public string? CommandProfileVersion { get; init; }

    public string? CommandProfileDigest { get; init; }
}

public sealed record DevelopmentApprovedApplySubject(
    Guid ProjectId,
    Guid TaskId,
    long ExpectedTaskVersion,
    string BaseCommit,
    string PatchHash,
    string ManifestHash,
    string ExpectedResultHash,
    string PatchArtifactReference,
    string ManifestArtifactReference,
    Guid PatchArtifactId = default,
    Guid ManifestArtifactId = default,
    string SubjectHash = "",
    string RepositoryIdentityHash = "",
    string BaseBranch = "",
    long PatchByteCount = 0,
    long ManifestByteCount = 0);

public sealed record DevelopmentOperationResult(
    Guid ProjectId,
    Guid? TaskId,
    Guid? AttemptId,
    Guid? ArtifactId,
    Guid OperationId,
    string Phase,
    string Outcome,
    string Status,
    long Version,
    long Sequence);

public sealed class DevelopmentEventSnapshot
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid? TaskId { get; init; }

    public required Guid? AttemptId { get; init; }

    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    public required long OccurredAtUtc { get; init; }

    public required Guid? OperationId { get; init; }

    public required string? OperationPhase { get; init; }

    public required string? Outcome { get; init; }

    /// <summary>
    ///     The sentence the event was written with, when it carries one — why a task was blocked, why validation
    ///     failed, why a workflow's fix loop sent an approved task back.
    /// </summary>
    /// <remarks>
    ///     The only member of the detail document that leaves this store: it is authored or sanitized and bounded at
    ///     every write site, which the rest of the document is not.
    /// </remarks>
    public string? Reason { get; init; }
}

public sealed record DevelopmentExecutionSnapshot
{
    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid AttemptId { get; init; }

    public required Guid? SelectedFolderId { get; init; }

    public required string RepositoryIdentityHash { get; init; }

    public required string BaseBranch { get; init; }

    public required DevelopmentEgressPolicy EgressPolicy { get; init; }

    public required int ConfigurationVersion { get; init; }

    public required bool TrustedRepositoryAcknowledged { get; init; }

    public required int? TrustedRepositoryPolicyVersion { get; init; }

    public required long? TrustedRepositoryAcknowledgedAtUtc { get; init; }

    public required int? MaxTokens { get; init; }

    public required int? MaxDurationSeconds { get; init; }

    public required string Title { get; init; }

    public required string Requirements { get; init; }

    public required string AcceptanceCriteriaJson { get; init; }

    public required DevelopmentTaskStatus TaskStatus { get; init; }

    public required long TaskVersion { get; init; }

    public required DevelopmentAttemptRole AttemptRole { get; init; }

    public required DevelopmentAttemptStatus AttemptStatus { get; init; }

    public required string ModelId { get; init; }

    public required string Provider { get; init; }

    public required long AttemptVersion { get; init; }

    public required string? CommandProfileJson { get; init; }

    /// <summary>
    ///     What the last request for changes on this task said, or nothing when it has never been asked for rework.
    /// </summary>
    /// <remarks>
    ///     Resolved from the task's own event log rather than from a column, so it costs no migration and reads the
    ///     same sentence a reviewer wrote and a workflow's fix loop wrote.
    /// </remarks>
    public string? PreviousRoundFeedback { get; init; }

    /// <summary>
    ///     The rule-set text a Development workflow injected onto this task, or nothing when no workflow drives it.
    /// </summary>
    /// <remarks>
    ///     Resolved from the task's own event log exactly as <see cref="PreviousRoundFeedback" /> is, so it costs no
    ///     migration and reaches the coder and the reviewer through the one channel both already read. Bounded by the
    ///     node run that applied it, not by the task: the workflow records its resolution on every dispatch — an EMPTY
    ///     one included — and an empty one again when it settles the node run, so a later workflow that resolves no
    ///     policy and a manual round afterwards both answer nothing rather than replaying a dead snapshot.
    /// </remarks>
    public string? WorkflowPolicyText { get; init; }

    /// <summary>
    ///     The last thing a PERSON told this task to do differently, or nothing. Read from the task's own event log
    ///     like the two above, and disjoint from <see cref="PreviousRoundFeedback" />: a row is never both.
    /// </summary>
    /// <remarks>
    ///     It carries no STATUS gate, and that is the difference. A Dev Mode task's requirements are immutable — no
    ///     PUT, no PATCH — so an operator's retry reason is the ONLY way to amend a mis-specified task, and an
    ///     amendment that expired at the next event would be undone by the very next reviewer round. It is bounded in
    ///     time by the same row <see cref="WorkflowPolicyText" /> is: an instruction stops governing when the node-run
    ///     attempt that carried it stops driving the task, and does not follow the task into a second node run.
    /// </remarks>
    public string? OperatorInstruction { get; init; }
}

/// <summary>
///     One rule set as a workflow's policy event names it. The hash is what lets the Dev Mode audit and the workflow's
///     own node-run resolution be checked against each other; the text itself lives in the event's <c>policyText</c>.
/// </summary>
public sealed record DevelopmentWorkflowRuleSetReference(Guid Id, string Name, string ContentSha256);

public sealed record DevelopmentProjectSnapshot
{
    public required Guid Id { get; init; }

    public required string Objective { get; init; }

    public required Guid? SelectedFolderId { get; init; }

    public required string RepositoryIdentityHash { get; init; }

    public required string BaseBranch { get; init; }

    public required DevelopmentProjectStatus Status { get; init; }

    public required DevelopmentEgressPolicy EgressPolicy { get; init; }

    public required string? CoderModelId { get; init; }

    public required string? ReviewerModelId { get; init; }

    public required int? MaxTokens { get; init; }

    public required int? MaxDurationSeconds { get; init; }

    public required int ConfigurationVersion { get; init; }

    public required bool TrustedRepositoryAcknowledged { get; init; }

    public required int? TrustedRepositoryPolicyVersion { get; init; }

    public required long? TrustedRepositoryAcknowledgedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }

    public required string? CommandProfileJson { get; init; }
}

public sealed record DevelopmentTaskSnapshot
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required string Title { get; init; }

    public required string Requirements { get; init; }

    public required string AcceptanceCriteriaJson { get; init; }

    public required DevelopmentTaskStatus Status { get; init; }

    public required int CurrentReviewRound { get; init; }

    public required int MaxReviewRounds { get; init; }

    public required string? BlockedReason { get; init; }

    public required long? BlockedAtUtc { get; init; }

    public required string? ApprovedSubjectHash { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }
}

public sealed class DevelopmentAttemptSnapshot
{
    public required Guid Id { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid? PredecessorAttemptId { get; init; }

    public required DevelopmentAttemptRole Role { get; init; }

    public required string ModelId { get; init; }

    public required string Provider { get; init; }

    public required DevelopmentAttemptStatus Status { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? EndedAtUtc { get; init; }

    public required string? TerminalReason { get; init; }

    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }

    public required long Version { get; init; }
}

public sealed class DevelopmentArtifactSnapshot
{
    public required Guid Id { get; init; }

    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid? AttemptId { get; init; }

    public required DevelopmentArtifactKind Kind { get; init; }

    public required int SchemaVersion { get; init; }

    public required string? ManagedReference { get; init; }

    public required string ContentHash { get; init; }

    public required long ByteCount { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required string? BaseCommit { get; init; }

    public required string? SubjectHash { get; init; }

    public required string? ChangedFilesManifestHash { get; init; }

    public required ReadOnlyMemory<byte>? InputArtifactIdsJson { get; init; }

    public required string? CommandProfileVersion { get; init; }

    public required bool IsValid { get; init; }

    public required string? CommandProfileDigest { get; init; }
}

public interface IDevelopmentStore
{
    Task<DevelopmentOperationResult> CreateProjectAsync(DevelopmentCreateProjectCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds a task to an existing project. INTERNAL: no endpoint reaches this — the caller is workflow
    ///     decomposition.
    /// </summary>
    /// <remarks>
    ///     Decomposition gives every implementation child its own task inside the project the run was already
    ///     authorised against. Idempotent on <see cref="DevelopmentCreateTaskCommand.OperationId" /> like every other
    ///     Development mutation, which is what makes it safe to call before the pointer to the new task is written: a
    ///     caller that crashes in between re-asks with the same operation identity and is handed the SAME task rather
    ///     than orphaning it and creating another.
    /// </remarks>
    /// <exception cref="DevelopmentNotFoundException">The project does not exist.</exception>
    Task<DevelopmentOperationResult> CreateTaskAsync(DevelopmentCreateTaskCommand command, CancellationToken cancellationToken = default);

    Task<DevelopmentOperationResult> StartAttemptAsync(DevelopmentStartAttemptCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> TerminalizeAttemptAsync(DevelopmentTerminalizeAttemptCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> TransitionTaskAsync(DevelopmentTransitionTaskCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> StartValidationAsync(DevelopmentStartValidationCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> InvalidateEvidenceAsync(DevelopmentInvalidateEvidenceCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> FinalizeValidationAsync(DevelopmentFinalizeValidationCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> FinalizeReviewAsync(DevelopmentFinalizeReviewCommand command, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> AttachArtifactAsync(DevelopmentAttachArtifactCommand command, CancellationToken cancellationToken = default);
    Task<int> ReconcileRunningAttemptsAsync(string sanitizedReason, CancellationToken cancellationToken = default);
    Task<int> ReconcileIncompleteValidationsAsync(string sanitizedReason, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult?> FindOperationAsync(Guid projectId, Guid operationId, string phase, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> RecordApplyStartedAsync(Guid operationId, DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> CompleteApplyAsync(Guid operationId, DevelopmentApprovedApplySubject subject, CancellationToken cancellationToken = default);
    Task<DevelopmentOperationResult> BlockApplyAsync(Guid operationId, DevelopmentApprovedApplySubject subject, string sanitizedReason, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records that the managed workspace for <paramref name="attemptId" /> carries COMMITTED files whose names
    ///     mark them as credential-bearing, as an operator-visible event. Non-blocking: the attempt proceeds.
    /// </summary>
    /// <remarks>
    ///     Idempotent per attempt — the operation is keyed on the attempt id and its own phase, so a second prepare of
    ///     the same attempt (validation re-prepares the coder's workspace) returns the first result rather than
    ///     writing a duplicate event. The paths go into the event's encrypted detail. On a backend with no mount layer
    ///     this event is the WHOLE control: the engine can see the committed secret but cannot stop the repository's
    ///     own build from reading it, so making it visible is all it can honestly do.
    /// </remarks>
    Task<DevelopmentOperationResult> RecordWorkspaceSecretsAsync(Guid taskId,
        Guid attemptId,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the rule-set text a Development workflow resolved for the node run that drives
    ///     <paramref name="taskId" />, as the channel the coder and reviewer prompts read it back through.
    /// </summary>
    /// <remarks>
    ///     Idempotent per <paramref name="operationId" />, derived deterministically from the run and node, so a node
    ///     run re-bound to the same task after a crash appends no second injection. The text goes into the event's
    ///     encrypted detail: a SNAPSHOT the node run already made, since this store never reads a rule set and what it
    ///     is handed is what the workflow's audit names by hash. A BLANK text with no rule sets is the clear, and the
    ///     snapshot reads the latest row — recording one revokes the policy for every round after it, bounding the injection without a second event type.
    /// </remarks>
    Task<DevelopmentOperationResult> RecordWorkflowPolicyAsync(Guid taskId,
        Guid operationId,
        string policyText,
        IReadOnlyList<DevelopmentWorkflowRuleSetReference> ruleSets,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevelopmentEventSnapshot>> ListEventsAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<DevelopmentExecutionSnapshot> GetExecutionSnapshotAsync(Guid attemptId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentProjectSnapshot>> ListProjectsAsync(CancellationToken cancellationToken = default);
    Task<DevelopmentProjectSnapshot> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<DevelopmentProjectSnapshot> ReconnectProjectRepositoryAsync(Guid projectId,
        Guid selectedFolderId,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes a command profile onto a project created before the profile column existed, and bumps its
    ///     configuration version so the change is visible to anything tracking project configuration.
    /// </summary>
    /// <remarks>
    ///     Only ever fills a null. A project that already carries a profile is returned untouched, because that
    ///     profile is the operator-confirmed agreement for the life of the project and a backfill must never be able
    ///     to replace it — including when two backfill passes race.
    /// </remarks>
    Task<DevelopmentProjectSnapshot> BackfillCommandProfileAsync(Guid projectId,
        string commandProfileJson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<DevelopmentTaskSnapshot> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<DevelopmentArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);
}

public sealed class DevelopmentConcurrencyException : InvalidOperationException
{
    public DevelopmentConcurrencyException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

public sealed class DevelopmentInvalidTransitionException : InvalidOperationException
{
    public DevelopmentInvalidTransitionException(string message) : base(message)
    {
    }
}
