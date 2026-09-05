namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public static class DevelopmentOperationPhases
{
    public const string Completed = "Completed";
    public const string ApplyStarted = "ApplyStarted";
    public const string ApplyCompleted = "ApplyCompleted";
    public const string ApplyBlocked = "ApplyBlocked";
}

public sealed record DevelopmentCreateProjectCommand(
    Guid ProjectId,
    Guid TaskId,
    Guid OperationId,
    string Objective,
    Guid SelectedFolderId,
    string RepositoryIdentityHash,
    string BaseBranch,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    DevelopmentEgressPolicy EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
    string? CoderModelId = null,
    string? ReviewerModelId = null,
    int MaxReviewRounds = 3,
    int ConfigurationVersion = 1,
    bool TrustedRepositoryAcknowledged = false,
    int? TrustedRepositoryPolicyVersion = null,
    long? TrustedRepositoryAcknowledgedAtUtc = null,
    int? MaxTokens = null,
    int? MaxDurationSeconds = null,
    string? CommandProfileJson = null);

/// <summary>
///     One more task on a project that already exists — the task-shaped half of
///     <see cref="DevelopmentCreateProjectCommand" />, with everything the project owns (repository, trust
///     acknowledgement, models, egress policy, command profile) inherited by living in it.
/// </summary>
public sealed record DevelopmentCreateTaskCommand(
    Guid ProjectId,
    Guid TaskId,
    Guid OperationId,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    int MaxReviewRounds = 3);

public sealed record DevelopmentStartAttemptCommand(
    Guid TaskId,
    Guid AttemptId,
    Guid OperationId,
    DevelopmentAttemptRole Role,
    string ModelId,
    string Provider,
    long ExpectedTaskVersion,
    Guid? PredecessorAttemptId = null);

public sealed record DevelopmentTerminalizeAttemptCommand(
    Guid AttemptId,
    Guid OperationId,
    DevelopmentAttemptStatus Status,
    long ExpectedAttemptVersion,
    string? TerminalReason = null,
    long? InputTokens = null,
    long? OutputTokens = null);

public sealed record DevelopmentTransitionTaskCommand(
    Guid TaskId,
    Guid OperationId,
    DevelopmentTaskStatus TargetStatus,
    long ExpectedTaskVersion,
    string? Reason = null,
    string? ApprovedSubjectHash = null,

    /// <summary>
    ///     That a PERSON wrote <see cref="Reason" />, rather than a reviewer, a gate or a workflow's own fix loop. It
    ///     is what lets the prompts rank it: an operator's sentence amends the task's immutable requirements, and a
    ///     reviewer's does not.
    /// </summary>
    bool OperatorDirected = false,

    /// <summary>
    ///     Raises the task's <c>MaxReviewRounds</c> by one, and is the ONLY thing that opens the single edge out of
    ///     <c>Blocked</c>. A person who retries a workflow node stopped at "all N rounds used" is buying the task the
    ///     round it needs, the same way that click already buys the node one more attempt — and a task let out of
    ///     Blocked without one would spend a whole coder round to be stood down again on the same sentence.
    ///     <para>
    ///         CALLER-TRUSTED: the store applies this without verifying that an operator did anything, so only an
    ///         operator-DECISION path may set it — today <c>DevWorkflowDevTaskExecutor.CarryOperatorRetryAsync</c>,
    ///         gated on <c>DevWorkflowNodeInputs.IsOperatorRetry</c>. A new caller must be gated the same way.
    ///     </para>
    /// </summary>
    bool WidenReviewRounds = false);

public sealed record DevelopmentStartValidationCommand(
    Guid TaskId,
    Guid OperationId,
    long ExpectedTaskVersion);

public sealed record DevelopmentInvalidateEvidenceCommand(
    Guid TaskId,
    Guid OperationId,
    long ExpectedTaskVersion,
    string SanitizedReason);

public sealed record DevelopmentFinalizeValidationCommand(
    DevelopmentAttachArtifactCommand Artifact,
    Guid OperationId,
    long ExpectedTaskVersion,
    DevelopmentTaskStatus TargetStatus,
    string? SanitizedReason = null);

public sealed record DevelopmentFinalizeReviewCommand(
    DevelopmentAttachArtifactCommand Artifact,
    Guid OperationId,
    long ExpectedTaskVersion,
    long ExpectedAttemptVersion,
    DevelopmentTaskStatus TargetStatus,
    string? ApprovedSubjectHash,
    string? SanitizedReason,
    long? InputTokens,
    long? OutputTokens);

public sealed record DevelopmentAttachArtifactCommand(
    Guid ArtifactId,
    Guid ProjectId,
    Guid TaskId,
    Guid? AttemptId,
    Guid OperationId,
    DevelopmentArtifactKind Kind,
    int SchemaVersion,
    string ContentHash,
    long ByteCount,
    ReadOnlyMemory<byte>? ContentJson = null,
    string? ManagedReference = null,
    string? BaseCommit = null,
    string? SubjectHash = null,
    string? ChangedFilesManifestHash = null,
    ReadOnlyMemory<byte>? InputArtifactIdsJson = null,
    string? CommandProfileVersion = null,
    string? CommandProfileDigest = null);

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

public sealed record DevelopmentEventSnapshot(
    Guid Id,
    Guid ProjectId,
    Guid? TaskId,
    Guid? AttemptId,
    long Sequence,
    string EventType,
    long OccurredAtUtc,
    Guid? OperationId,
    string? OperationPhase,
    string? Outcome,
    /// <summary>
    ///     The sentence the event was written with, when it carries one — why a task was blocked, why validation
    ///     failed, why a workflow's fix loop sent an approved task back. The only member of the detail document that
    ///     leaves this store: it is authored or sanitized and bounded at every write site, which the rest of the
    ///     document is not.
    /// </summary>
    string? Reason = null);

public sealed record DevelopmentExecutionSnapshot(
    Guid ProjectId,
    Guid TaskId,
    Guid AttemptId,
    Guid? SelectedFolderId,
    string RepositoryIdentityHash,
    string BaseBranch,
    DevelopmentEgressPolicy EgressPolicy,
    int ConfigurationVersion,
    bool TrustedRepositoryAcknowledged,
    int? TrustedRepositoryPolicyVersion,
    long? TrustedRepositoryAcknowledgedAtUtc,
    int? MaxTokens,
    int? MaxDurationSeconds,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    DevelopmentTaskStatus TaskStatus,
    long TaskVersion,
    DevelopmentAttemptRole AttemptRole,
    DevelopmentAttemptStatus AttemptStatus,
    string ModelId,
    string Provider,
    long AttemptVersion,
    string? CommandProfileJson,
    /// <summary>
    ///     What the last request for changes on this task said, or nothing when it has never been asked for rework.
    ///     Resolved from the task's own event log rather than from a column, so it costs no migration and reads the same
    ///     sentence a reviewer wrote and a workflow's fix loop wrote.
    /// </summary>
    string? PreviousRoundFeedback = null,

    /// <summary>
    ///     The rule-set text a Development workflow injected onto this task, or nothing when no workflow drives it.
    ///     Resolved from the task's own event log rather than from a column, exactly as
    ///     <see cref="PreviousRoundFeedback" /> is, so it costs no migration and reaches the coder and the reviewer
    ///     through the one channel both already read.
    ///     <para>
    ///         Bounded by the node run that applied it, not by the task: the workflow records its resolution on every
    ///         dispatch — an EMPTY one included — and records an empty one again when it settles the node run. So a
    ///         later workflow that resolves no policy, and a manual Dev Mode round after the workflow has finished,
    ///         both answer nothing rather than replaying a snapshot nothing is enforcing any more.
    ///     </para>
    /// </summary>
    string? WorkflowPolicyText = null,

    /// <summary>
    ///     The last thing a PERSON told this task to do differently, or nothing. Read from the task's own event log
    ///     like the two above, and disjoint from <see cref="PreviousRoundFeedback" />: whichever of the two a row is,
    ///     it is never both.
    ///     <para>
    ///         Free of the STATUS gate <see cref="PreviousRoundFeedback" /> carries, and that is the difference. A Dev
    ///         Mode task's requirements are immutable — there is no PUT and no PATCH — so an operator's retry reason is
    ///         the ONLY way to amend a task that was mis-specified, and an amendment that expired at the next event
    ///         would be undone by the very next reviewer round. Live on 2026-09-04 that is exactly what happened: the
    ///         operator moved a test out of a base-committed file the test-write policy protects, and the reviewer,
    ///         reading only the original requirements, sent it straight back in.
    ///     </para>
    ///     <para>
    ///         Bounded in time by the same row <see cref="WorkflowPolicyText" /> is bounded by, so the two fields on
    ///         this record have one lifetime rather than two: an instruction stops governing when the node-run attempt
    ///         that carried it stops driving the task. It does not follow the task into a second node run.
    ///     </para>
    /// </summary>
    string? OperatorInstruction = null);

/// <summary>
///     One rule set as a workflow's policy event names it. The hash is what lets the Dev Mode audit and the workflow's
///     own node-run resolution be checked against each other; the text itself lives in the event's <c>policyText</c>.
/// </summary>
public sealed record DevelopmentWorkflowRuleSetReference(Guid Id, string Name, string ContentSha256);

public sealed record DevelopmentProjectSnapshot(
    Guid Id,
    string Objective,
    Guid? SelectedFolderId,
    string RepositoryIdentityHash,
    string BaseBranch,
    DevelopmentProjectStatus Status,
    DevelopmentEgressPolicy EgressPolicy,
    string? CoderModelId,
    string? ReviewerModelId,
    int? MaxTokens,
    int? MaxDurationSeconds,
    int ConfigurationVersion,
    bool TrustedRepositoryAcknowledged,
    int? TrustedRepositoryPolicyVersion,
    long? TrustedRepositoryAcknowledgedAtUtc,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version,
    string? CommandProfileJson);

public sealed record DevelopmentTaskSnapshot(
    Guid Id,
    Guid ProjectId,
    string Title,
    string Requirements,
    string AcceptanceCriteriaJson,
    DevelopmentTaskStatus Status,
    int CurrentReviewRound,
    int MaxReviewRounds,
    string? BlockedReason,
    long? BlockedAtUtc,
    string? ApprovedSubjectHash,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version);

public sealed record DevelopmentAttemptSnapshot(
    Guid Id,
    Guid TaskId,
    Guid? PredecessorAttemptId,
    DevelopmentAttemptRole Role,
    string ModelId,
    string Provider,
    DevelopmentAttemptStatus Status,
    long? StartedAtUtc,
    long? EndedAtUtc,
    string? TerminalReason,
    long? InputTokens,
    long? OutputTokens,
    long Version);

public sealed record DevelopmentArtifactSnapshot(
    Guid Id,
    Guid ProjectId,
    Guid TaskId,
    Guid? AttemptId,
    DevelopmentArtifactKind Kind,
    int SchemaVersion,
    string? ManagedReference,
    string ContentHash,
    long ByteCount,
    long CreatedAtUtc,
    string? BaseCommit,
    string? SubjectHash,
    string? ChangedFilesManifestHash,
    ReadOnlyMemory<byte>? InputArtifactIdsJson,
    string? CommandProfileVersion,
    bool IsValid,
    string? CommandProfileDigest);

public interface IDevelopmentStore
{
    Task<DevelopmentOperationResult> CreateProjectAsync(DevelopmentCreateProjectCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds a task to an existing project. INTERNAL: no endpoint reaches this — the caller is workflow
    ///     decomposition, which gives every implementation child its own task inside the project the run was already
    ///     authorised against.
    ///     <para>
    ///         Idempotent on <see cref="DevelopmentCreateTaskCommand.OperationId" /> like every other Development
    ///         mutation, and that is what makes it safe to call before the pointer to the new task is written: a caller
    ///         that crashes in between re-asks with the same operation identity and is handed the SAME task rather than
    ///         orphaning it and creating another.
    ///     </para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">The project does not exist.</exception>
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
    ///     <para>
    ///         Idempotent per attempt — the operation is keyed on the attempt id and its own phase, so a second prepare
    ///         of the same attempt (validation re-prepares the coder's workspace) returns the first result rather than
    ///         writing a duplicate event.
    ///     </para>
    ///     <para>
    ///         The paths go into the event's encrypted detail. On a backend with no mount layer this event is the WHOLE
    ///         control: the engine can see the committed secret but cannot stop the repository's own build from reading
    ///         it, so making it visible is all it can honestly do.
    ///     </para>
    /// </summary>
    Task<DevelopmentOperationResult> RecordWorkspaceSecretsAsync(Guid taskId,
        Guid attemptId,
        IReadOnlyList<string> repositoryRelativePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the rule-set text a Development workflow resolved for the node run that drives
    ///     <paramref name="taskId" />, as the channel the coder and reviewer prompts read it back through.
    ///     <para>
    ///         Idempotent per <paramref name="operationId" />, which the caller derives deterministically from its run
    ///         and node — so a node run re-bound to the same task after a crash returns the first result rather than
    ///         appending a second injection of the same policy.
    ///     </para>
    ///     <para>
    ///         The text goes into the event's encrypted detail. It is a SNAPSHOT the node run already made: this store
    ///         never reads a rule set, and what it is handed is what the workflow's audit permanently names by hash.
    ///     </para>
    ///     <para>
    ///         A BLANK <paramref name="policyText" /> with no rule sets is the clear, and the snapshot reads the latest
    ///         row — so recording one revokes the policy for every round after it. That is how the injection is bounded
    ///         in time without a second event type: a workflow that resolves nothing, and one that has settled its node
    ///         run, both say so on the same log the injection was written to.
    ///     </para>
    /// </summary>
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
    ///     Writes a command profile onto a project created before the profile column existed, and bumps its configuration
    ///     version so the change is visible to anything tracking project configuration.
    ///     <para>
    ///         Only ever fills a null. A project that already carries a profile is returned untouched, because that
    ///         profile is the operator-confirmed agreement for the life of the project and a backfill must never be able
    ///         to replace it — including when two backfill passes race.
    ///     </para>
    /// </summary>
    Task<DevelopmentProjectSnapshot> BackfillCommandProfileAsync(Guid projectId,
        string commandProfileJson,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevelopmentTaskSnapshot>> ListTasksAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<DevelopmentTaskSnapshot> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentAttemptSnapshot>> ListAttemptsAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DevelopmentArtifactSnapshot>> ListArtifactsAsync(Guid taskId, CancellationToken cancellationToken = default);
    Task<DevelopmentArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);
}

public sealed class DevelopmentConcurrencyException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

public sealed class DevelopmentInvalidTransitionException(string message) : InvalidOperationException(message);
