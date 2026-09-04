namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The sentinel <see cref="Any" /> version. The dispatcher moves run and node-run status while a human HTTP action
///     may be writing a decision on the same run, so a status move — which has no lost update to protect against —
///     passes <see cref="Any" /> and never loses the race to a content write.
/// </summary>
public static class DevWorkflowVersions
{
    public const long Any = -1;
}

/// <summary>
///     The node-status tallies a work-item or run summary carries, so a list page can draw progress without a per-row
///     query. <see cref="BlockingGateNodeRunId" /> is the first node-run waiting on a human, in sequence order.
/// </summary>
public sealed record DevWorkflowNodeCounters(
    int Queued,
    int Running,
    int Completed,
    int Total,
    int PendingDecisionCount,
    Guid? BlockingGateNodeRunId)
{
    public static DevWorkflowNodeCounters Empty { get; } = new(0, 0, 0, 0, 0, null);
}

public sealed record DevWorkflowWorkItemSnapshot(
    Guid Id,
    string Title,
    string Request,
    DevWorkflowWorkItemStatus Status,
    Guid? DevelopmentProjectId,
    Guid? LatestRunId,
    DevWorkflowRunStatus? LatestRunStatus,
    string? LatestRunDefinitionName,
    DevWorkflowNodeCounters LatestRunNodes,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version);

/// <summary>What work-item detail embeds as its run list: a run without the graph blob, plus its node counters.</summary>
public sealed record DevWorkflowRunSummary(
    Guid Id,
    Guid WorkItemId,
    Guid DefinitionId,
    string? DefinitionName,
    DevWorkflowRunStatus Status,
    DevWorkflowNodeCounters Nodes,
    string? FailureClass,
    long? StartedAtUtc,
    long? EndedAtUtc,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record DevWorkflowRunSnapshot(
    Guid Id,
    Guid WorkItemId,
    Guid DefinitionId,
    int DefinitionVersion,
    string DefinitionGraphHash,
    string GraphJson,
    int GraphRevision,
    DevWorkflowRunStatus Status,
    long LastSequence,
    string? FailureClass,
    string? TerminalReason,
    long? StartedAtUtc,
    long? EndedAtUtc,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version);

/// <summary>What the definition list returns: no graph blob, so listing never decrypts one.</summary>
public sealed record DevWorkflowDefinitionSummary(
    Guid Id,
    string Name,
    string GraphHash,
    int NodeCount,
    DevWorkflowDefinitionSource Source,
    string? SeedSlug,
    bool Archived,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record DevWorkflowDefinitionSnapshot(
    Guid Id,
    string Name,
    string GraphJson,
    string GraphHash,
    int NodeCount,
    DevWorkflowDefinitionSource Source,
    string? SeedSlug,
    bool Archived,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     One rule set in full, body included. <see cref="ContentSha256" /> is computed store-side alongside the body, so
///     the hash and the text can never describe different documents.
/// </summary>
public sealed record DevWorkflowRuleSetSnapshot(
    Guid Id,
    string Name,
    string? Description,
    string ScopeJson,
    bool Enabled,
    string Body,
    string ContentSha256,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     A rule set WITHOUT its body — everything the list page draws and everything the resolver matches on. The body is
///     the one encrypted column here, so a feed that never asks for it never decrypts one.
/// </summary>
public sealed record DevWorkflowRuleSetSummary(
    Guid Id,
    string Name,
    string? Description,
    string ScopeJson,
    bool Enabled,
    string ContentSha256,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>The canonical node-run field set. <see cref="WorkSessionAvailable" /> is read from the other family, never stored.</summary>
public sealed record DevWorkflowNodeRunSnapshot(
    Guid Id,
    Guid RunId,
    string NodeKey,
    DevWorkflowNodeType NodeType,
    int Attempt,
    int MaxAttempts,
    int SessionResumes,
    DevWorkflowNodeRunStatus Status,
    string? QueueReason,
    DevWorkflowDecisionKind? PendingDecisionKind,
    long Sequence,
    Guid? WorkSessionId,
    bool WorkSessionAvailable,
    Guid? AgentDefinitionId,
    Guid? DevelopmentProjectId,
    Guid? DevelopmentTaskId,
    string? InputJson,
    string? OutputJson,
    string? PolicyResolutionJson,
    Guid? MaterializedFromNodeRunId,
    int? MaterializationIndex,
    string? FailureClass,
    string? TerminalReason,
    long? QueuedAtUtc,
    long? StartedAtUtc,
    long? EndedAtUtc,
    long CreatedAtUtc,

    // The twelve cost-telemetry columns, trailing and optional so a caller composing a node run by hand — a test, a
    // fake store — keeps compiling and reads back exactly what a row written before this slice reads back: nulls.
    long? InputTokens = null,
    long? OutputTokens = null,
    long? ReasoningTokens = null,
    long? EstimatedInputTokens = null,
    int? ProviderCalls = null,
    int? ToolCalls = null,
    long? ToolSchemaTokens = null,
    string? ToolNamesJson = null,
    long? AgentTurnMs = null,
    string? ServedModelName = null,
    string? RouteJson = null,
    int? WorkSessionSteps = null);

/// <summary>
///     What one node-run attempt spent and where it routed, collected at the terminal-or-blocked transition and applied
///     to the row by <see cref="IDevWorkflowStore.TransitionNodeRunAsync" />. Every member is optional: a member left
///     null leaves its column untouched, which is how a node run with no work session and no development task still
///     records a route beside twelve nulls.
///     <para>
///         Metadata only — counts, a served model name, structural node keys and tool NAMES. No prompt, no tool
///         argument, no tool result and no transcript may ever be added here.
///         <see cref="RouteJson" /> and <see cref="ToolNamesJson" /> arrive already serialized because this record
///         crosses into persistence, where neither the graph projection nor the collector's name set is visible.
///     </para>
/// </summary>
public sealed record DevWorkflowNodeTelemetry(
    long? InputTokens = null,
    long? OutputTokens = null,
    long? ReasoningTokens = null,
    long? EstimatedInputTokens = null,
    int? ProviderCalls = null,
    int? ToolCalls = null,
    long? ToolSchemaTokens = null,
    string? ToolNamesJson = null,
    long? AgentTurnMs = null,
    string? ServedModelName = null,
    string? RouteJson = null,
    int? WorkSessionSteps = null);

public sealed record DevWorkflowRunEventSnapshot(
    Guid Id,
    Guid RunId,
    Guid? NodeRunId,
    long Sequence,
    string EventType,
    string? DetailJson,
    Guid? OperationId,
    string? Outcome,
    long OccurredAtUtc);

/// <summary><see cref="IsLatest" /> is computed (max version per lineage) and ships on the wire, so no client derives it.</summary>
public sealed record DevWorkflowArtifactSnapshot(
    Guid Id,
    Guid RunId,
    Guid LineageId,
    string ProducingNodeKey,
    Guid ProducedByNodeRunId,
    string Name,
    int Version,
    bool IsLatest,
    DevWorkflowArtifactKind Kind,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    bool IsValid,
    bool IsStale,
    long? StaleSinceSequence,
    Guid? StaleBecauseArtifactId,
    string? StaleReason,
    string ManagedReference,
    long Sequence,
    long CreatedAtUtc);

public sealed record DevWorkflowDecisionSnapshot(
    Guid Id,
    Guid RunId,
    Guid NodeRunId,
    int Attempt,
    DevWorkflowDecisionKind Decision,
    string? Comment,
    string? PayloadJson,
    string? DecidedBySubject,
    Guid OperationId,
    long Sequence,
    long DecidedAtUtc);

/// <summary>
///     One row per node-run the host left mid-flight, carrying enough detail for the runtime to rebuild its dispatch
///     table without a follow-up read per row. <see cref="Status" /> is the status the node-run held <em>before</em> the
///     collapse — what it was doing is the useful fact; where it landed is always <c>Pending</c>, unless a repair moved
///     it further.
/// </summary>
public sealed record DevWorkflowReconciledNodeRun(
    Guid NodeRunId,
    Guid RunId,
    string NodeKey,
    DevWorkflowNodeType NodeType,
    DevWorkflowNodeRunStatus Status,
    int Attempt,
    Guid? WorkSessionId);

/// <summary>
///     One judged node-run: the row as the caller observed it, and what to do with it once the collapse has confirmed
///     it is still that row.
///     <para>
///         The expectation is the whole point. A verdict is only true of the state it was decided from, so the collapse
///         matches each one against the live row and takes only the rows that still agree. A row that moved under the
///         caller — or one that became stranded after the caller read — is left exactly as it is: collapsing it
///         unjudged would strand it at <c>Pending</c>, where nothing would ever judge it again, and repairing it from
///         stale evidence would spend an attempt on a state it is no longer in.
///     </para>
/// </summary>
public sealed record DevWorkflowNodeRunVerdict(
    Guid NodeRunId,
    DevWorkflowNodeRunStatus ObservedStatus,
    int ObservedAttempt,
    Guid? ObservedWorkSessionId,
    IReadOnlyList<TransitionDevWorkflowNodeRunCommand> Repairs);

/// <summary>
///     Turns a reconciliation into a SETTLING pass: every stranded node-run no verdict matched is blocked for a human
///     instead of being left as it is.
///     <para>
///         The blocked state is this record's business rather than the caller's, because it is what the pass promises:
///         a settling pass leaves no node-run stranded, and a row that is neither dispatchable nor waiting on a person
///         is one nothing will ever pick up. So the row lands <c>Blocked</c> with an <c>Abandon</c> decision pending
///         and its work item blocked with it — costing no attempt, which is the only honest price for a row nobody
///         could judge.
///     </para>
/// </summary>
public sealed record DevWorkflowUnjudgedNodeRunBlock(string FailureClass, string SanitizedReason);

/// <summary>
///     What one mutation committed: the watermark it allocated for its event, and the run row's post-commit version,
///     status and graph revision.
///     <para>
///         <see cref="SupersededArtifactId" /> is set only by <see cref="IDevWorkflowStore.AppendArtifactAsync" /> when
///         the write added a new version over an existing lineage. Its bytes are still on disk: the caller that owns the
///         blob store deletes them after the commit, because the schema project cannot reach the blob layer.
///     </para>
/// </summary>
public sealed record DevWorkflowMutationResult(
    Guid RunId,
    long Sequence,
    long Version,
    DevWorkflowRunStatus Status,
    int GraphRevision,
    Guid? SupersededArtifactId = null);

/// <summary>
///     What a work-item delete removed, and what it could not: the work sessions its agent node runs owned and the runs
///     whose artifact bytes are still on disk.
///     <para>
///         Answered by the delete rather than gathered before it, and that ordering is the point: the authoritative
///         live-run guard runs inside the same transaction, so a caller cannot destroy a transcript for a delete that is
///         then refused. It also makes the set complete by construction — a caller paging its own read would orphan
///         everything past the page.
///     </para>
/// </summary>
public sealed record DevWorkflowWorkItemDeletion(int RemovedRows, IReadOnlyList<Guid> RunIds, IReadOnlyList<Guid> WorkSessionIds);

public sealed record CreateDevWorkflowWorkItemCommand(
    Guid WorkItemId,
    string Title,
    string Request,
    Guid? DevelopmentProjectId = null);

public sealed record UpdateDevWorkflowWorkItemCommand(
    Guid WorkItemId,
    long ExpectedVersion,
    string? Title = null,
    string? Request = null,
    Guid? DevelopmentProjectId = null);

public sealed record CreateDevWorkflowDefinitionCommand(
    Guid DefinitionId,
    string Name,
    string GraphJson,
    int NodeCount,
    DevWorkflowDefinitionSource Source = DevWorkflowDefinitionSource.Manual,
    string? SeedSlug = null);

public sealed record UpdateDevWorkflowDefinitionCommand(
    Guid DefinitionId,
    int ExpectedVersion,
    string? Name = null,
    string? GraphJson = null,
    int? NodeCount = null);

public sealed record CreateDevWorkflowRuleSetCommand(
    Guid RuleSetId,
    string Name,
    string Body,
    string ScopeJson,
    string? Description = null,
    bool Enabled = true);

/// <summary>
///     A whole replacement, not a patch: the rule set is a document an operator edits as one, and a partial update
///     would have to invent a spelling for "clear the description" that a PUT body already has.
/// </summary>
public sealed record UpdateDevWorkflowRuleSetCommand(
    Guid RuleSetId,
    int ExpectedVersion,
    string Name,
    string Body,
    string ScopeJson,
    string? Description = null,
    bool Enabled = true);

/// <summary>
///     Starts a run. <see cref="NodeRuns" /> is the run's whole initial node set, created in the SAME transaction as the
///     run row: a run that committed without them could only be repaired by re-deriving the seeds, and the caller's
///     per-run inputs — which live nowhere but the entry rows — would be gone by then.
///     <para>
///         Empty is legal and means "rows only", which is what a store-level test wants; no runtime path uses it, and a
///         run with no node runs is one nothing will ever advance.
///     </para>
/// </summary>
public sealed record StartDevWorkflowRunCommand(
    Guid RunId,
    Guid WorkItemId,
    Guid DefinitionId,
    int DefinitionVersion,
    string DefinitionGraphHash,
    string GraphJson,
    IReadOnlyList<DevWorkflowNodeRunSeed>? NodeRuns = null);

/// <summary>
///     A run status move. <see cref="WorkItemStatus" /> lets the runtime write the work item's status inside the same
///     transaction that transitions the run, which is the only way the two can never disagree.
/// </summary>
public sealed record TransitionDevWorkflowRunCommand(
    Guid RunId,
    long ExpectedVersion,
    DevWorkflowRunStatus TargetStatus,
    Guid? OperationId = null,
    string? FailureClass = null,
    string? SanitizedReason = null,
    DevWorkflowWorkItemStatus? WorkItemStatus = null);

/// <summary>One node-run to create. <see cref="InputJson" /> on an entry node is what carries the operator's request to the first agent.</summary>
public sealed record DevWorkflowNodeRunSeed(
    Guid NodeRunId,
    string NodeKey,
    DevWorkflowNodeType NodeType,
    int MaxAttempts = 1,
    Guid? AgentDefinitionId = null,
    Guid? DevelopmentProjectId = null,
    string? InputJson = null,
    string? PolicyResolutionJson = null,
    Guid? MaterializedFromNodeRunId = null,
    int? MaterializationIndex = null);

/// <summary>
///     Creates node-runs on a run. A non-null <see cref="GraphJson" /> also rewrites the run's pinned graph and bumps
///     its revision in the same transaction — the dynamic-expansion path, recorded as <c>graph.changed</c>. A null one
///     is the initial materialization at run start, where the graph is already pinned and unchanged.
/// </summary>
public sealed record MaterializeDevWorkflowNodesCommand(
    Guid RunId,
    long ExpectedVersion,
    Guid OperationId,
    IReadOnlyList<DevWorkflowNodeRunSeed> NodeRuns,
    string? GraphJson = null);

/// <summary>
///     A node-run status move. <see cref="IncrementAttempt" /> is the retry-in-place path; the row is never duplicated.
///     <see cref="Outcome" /> overrides the outcome token derived from <see cref="TargetStatus" />, for the cases the
///     status alone cannot express (<c>timeout</c>, <c>interrupted</c>, <c>rejected</c>, <c>changes-requested</c>).
///     <para>
///         <see cref="ClearWorkSession" /> releases the session the row was driving, and pairs ONLY with a
///         <see cref="TargetStatus" /> of <c>Pending</c> — it belongs to a re-attempt, and a retry gets a NEW session
///         because resuming the one that just failed resumes its poisoned context. It is also what tells a
///         still-attached session apart from a finished one: a node run back at <c>Pending</c> with a session still on
///         it is one the host died under, and that session's answer still counts. Releasing it on any other target
///         would throw away the only pointer to the transcript the row's own result came from.
///     </para>
///     <para>
///         <see cref="InputJson" /> rewrites what the node run is asked to do, which only the cross-node fix loop does:
///         a re-attempt routed to an upstream node carries the failure that sent it there. <see cref="DetailJson" />
///         replaces the event detail this move would otherwise derive from <see cref="TerminalReason" />, for the one
///         move whose evidence is not on the row afterwards — a re-attempt clears the failure fields it is re-attempting
///         because of, so its <c>node.retry.scheduled</c> event is the only place that failure survives.
///     </para>
/// </summary>
public sealed record TransitionDevWorkflowNodeRunCommand(
    Guid RunId,
    Guid NodeRunId,
    long ExpectedVersion,
    DevWorkflowNodeRunStatus TargetStatus,
    Guid? OperationId = null,
    string? QueueReason = null,
    DevWorkflowDecisionKind? PendingDecisionKind = null,
    string? OutputJson = null,
    string? InputJson = null,
    string? FailureClass = null,
    string? TerminalReason = null,
    string? DetailJson = null,
    Guid? DevelopmentTaskId = null,
    bool IncrementAttempt = false,
    bool ClearWorkSession = false,
    string? Outcome = null,
    DevWorkflowWorkItemStatus? WorkItemStatus = null,

    // What the attempt this move settles cost. Set by the publishing decorator on a terminal, Blocked or
    // WaitingForApproval move and by nothing else, so no call site has to remember it.
    DevWorkflowNodeTelemetry? Telemetry = null);

/// <summary>
///     One cross-node retry route, as the single decision it is: the <c>node.retry.routed</c> event that records it and
///     every node-run reset that decision implies.
/// </summary>
/// <param name="Route">The routing event. Its run, expected version and operation id govern the whole command.</param>
/// <param name="Resets">
///     The node-run moves the route implies, applied IN ORDER after the event. Each must name <c>Route.RunId</c>.
/// </param>
public sealed record RouteDevWorkflowRetryCommand(AppendDevWorkflowEventCommand Route, IReadOnlyList<TransitionDevWorkflowNodeRunCommand> Resets);

public sealed record AttachDevWorkflowWorkSessionCommand(
    Guid RunId,
    Guid NodeRunId,
    long ExpectedVersion,
    Guid WorkSessionId,
    Guid? OperationId = null,
    bool CountsAsResume = false);

public sealed record AppendDevWorkflowArtifactCommand(
    Guid RunId,
    Guid ArtifactId,
    Guid NodeRunId,
    long ExpectedVersion,
    Guid OperationId,
    DevWorkflowArtifactKind Kind,
    string Name,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    string ManagedReference);

public sealed record RecordDevWorkflowArtifactUsesCommand(
    Guid RunId,
    Guid NodeRunId,
    long ExpectedVersion,
    Guid OperationId,
    IReadOnlyList<Guid> ArtifactIds);

public sealed record MarkDevWorkflowStaleCommand(
    Guid RunId,
    Guid SupersededArtifactId,
    Guid SupersedingArtifactId,
    long ExpectedVersion,
    Guid? OperationId = null,
    string StaleReason = DevWorkflowStaleReasons.SupersededInput);

/// <summary>
///     One human decision on one node-run attempt.
///     <para>
///         <see cref="MaxTotalAttempts" /> is the run-wide re-attempt budget this act has to fit inside. It travels on
///         the command because the store reads no options, and it is set only for <c>Retry</c> — the one decision that
///         authorises another attempt. Null means no budget applies to this act, which is every other decision.
///     </para>
///     <para>
///         Carried at all rather than left to the caller because the admission has to happen where the write does:
///         several blocked node runs each checked against the budget before the dispatcher settles any would each see
///         the same unspent budget and each pass, and the run would then spend more re-attempts than it allows.
///     </para>
/// </summary>
public sealed record RecordDevWorkflowDecisionCommand(
    Guid RunId,
    Guid DecisionId,
    Guid NodeRunId,
    long ExpectedVersion,
    Guid OperationId,
    DevWorkflowDecisionKind Decision,
    string? Comment = null,
    string? PayloadJson = null,
    string? DecidedBySubject = null,
    int? MaxTotalAttempts = null);

public sealed record AppendDevWorkflowEventCommand(
    Guid RunId,
    long ExpectedVersion,
    string EventType,
    Guid? NodeRunId = null,
    Guid? OperationId = null,
    string? Outcome = null,
    string? DetailJson = null);

/// <summary>The closed <c>stale_reason</c> token set. The <em>which</em> is <c>StaleBecauseArtifactId</c>, not this.</summary>
public static class DevWorkflowStaleReasons
{
    public const string SupersededInput = "superseded-input";
}

/// <summary>
///     The <c>event_type</c> catalog. It is a contract, not a convenience: the events tab and the per-node attempt list
///     read these, and the per-attempt history the single-row node-run schema does not keep lives in
///     <see cref="NodeRetryScheduled" /> and <see cref="WorkSessionAttached" />. The runtime extends this list by
///     amendment, never silently.
/// </summary>
public static class DevWorkflowEventTypes
{
    public const string RunCreated = "run.created";
    public const string RunStarted = "run.started";
    public const string RunPaused = "run.paused";
    public const string RunResumed = "run.resumed";

    /// <summary>The run stopped to ask a human. Distinct from <see cref="RunResumed" />, which says the opposite.</summary>
    public const string RunWaiting = "run.waiting";

    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
    public const string RunCancelled = "run.cancelled";
    public const string NodeMaterialized = "node.materialized";
    public const string NodeQueued = "node.queued";
    public const string NodeStarted = "node.started";
    public const string NodeCompleted = "node.completed";
    public const string NodeFailed = "node.failed";
    public const string NodeSkipped = "node.skipped";
    public const string NodeCancelled = "node.cancelled";
    public const string NodeRetryScheduled = "node.retry.scheduled";
    public const string NodeInterventionRequired = "node.intervention.required";
    public const string NodeRetryRouted = "node.retry.routed";
    public const string NodeInterrupted = "node.interrupted";
    public const string GateRequested = "gate.requested";
    public const string GateDecided = "gate.decided";
    public const string ArtifactCreated = "artifact.created";
    public const string ArtifactSuperseded = "artifact.superseded";
    public const string ArtifactUsed = "artifact.used";
    public const string ArtifactStaleMarked = "artifact.stale.marked";
    public const string GraphChanged = "graph.changed";
    public const string PolicyResolved = "policy.resolved";
    public const string WorkspaceSecretsDetected = "workspace.secrets.detected";
    public const string WorkSessionAttached = "worksession.attached";
    public const string WorkSessionUnavailable = "worksession.unavailable";
}

/// <summary>
///     The durable substrate for development workflows: one monotonic sequence per run, an append-only event log, and
///     optimistic concurrency on the run row.
///     <para>
///         Every mutation runs in one transaction that loads the run row, checks <c>ExpectedVersion</c> (unless it is
///         <see cref="DevWorkflowVersions.Any" />), allocates sequence values from the run's counter, appends one event,
///         and bumps the version. A non-null operation id resolves query-first: an operation already recorded returns
///         without writing, so a replayed step cannot double-append. The check runs both before and inside the
///         transaction, and the inner one is what makes a genuine race safe: a second writer blocks on SQLite's writer
///         lock, then sees the recorded operation and returns that result rather than an exception.
///     </para>
///     <para>
///         Legal state transitions are the runtime's to enforce, not this store's; this store provides
///         <see cref="DevWorkflowInvalidTransitionException" /> as the rejection channel and enforces only what the
///         database can — one live run per work item, one decision per node-run attempt, one owner per work session.
///     </para>
/// </summary>
public interface IDevWorkflowStore
{
    Task<DevWorkflowWorkItemSnapshot> CreateWorkItemAsync(CreateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowWorkItemSnapshot> UpdateWorkItemAsync(UpdateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The work-item page with each item's latest run and that run's node counters. Two queries total regardless of
    ///     row count — the page, then one pass over the node-runs of the listed runs — never one per row.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowWorkItemSnapshot>> ListWorkItemsAsync(DevWorkflowWorkItemStatus? status = null, CancellationToken cancellationToken = default);

    Task<DevWorkflowWorkItemSnapshot> GetWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the work item and every row below it in explicit dependency order, and answers what went — including
    ///     the work sessions and runs whose EXTERNAL state the caller must now release. The node connection runs without
    ///     <c>PRAGMA foreign_keys</c>, so the declared cascades never fire and the order is the only thing that keeps
    ///     the delete complete.
    ///     <para>
    ///         Refuses with <see cref="DevWorkflowRunInFlightException" /> while any of the item's runs is non-terminal,
    ///         checked inside the transaction so a run that starts mid-delete still wins. The caller learns what to
    ///         release only from a delete that COMMITTED, so a refusal can never arrive after the transcripts it was
    ///         protecting have already been destroyed.
    ///     </para>
    /// </summary>
    Task<DevWorkflowWorkItemDeletion> DeleteWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default);

    Task<DevWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateDevWorkflowDefinitionCommand command, CancellationToken cancellationToken = default);

    /// <summary>Never loads <c>graph_json</c>: the node count is the denormalized column, not a parse.</summary>
    Task<IReadOnlyList<DevWorkflowDefinitionSummary>> ListDefinitionsAsync(bool includeArchived = false, CancellationToken cancellationToken = default);

    Task<DevWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>Delete is an archive: runs that reference the definition, in flight or historical, are unaffected.</summary>
    Task<DevWorkflowDefinitionSnapshot> ArchiveDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default);

    Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(CreateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowRuleSetSnapshot> UpdateRuleSetAsync(UpdateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default);

    /// <summary>Never loads <c>body</c>: the list draws names and scopes, and the body is the encrypted column.</summary>
    Task<IReadOnlyList<DevWorkflowRuleSetSummary>> ListRuleSetsAsync(CancellationToken cancellationToken = default);

    Task<DevWorkflowRuleSetSnapshot> GetRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A HARD delete: a rule set is not referenced by a foreign key, and every node-run that applied one recorded
    ///     its <c>{id, name, contentSha256}</c> at materialization, so the audit survives the document.
    /// </summary>
    Task DeleteRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The resolver's read, at run start and at each materialization. Bodies included: the resolver SNAPSHOTS the
    ///     text it matched onto the node run, so what a node was given can never be re-derived from a document that has
    ///     since moved on.
    ///     <para>
    ///         ponytail: a full scan of the enabled rows with no cache. The scope is a JSON document with no column to
    ///         index on, the working set is a handful of rows on a single-operator node, and the call happens once per
    ///         run start and once per expansion — not per node run. Add a projected per-axis index table only if the
    ///         rule count ever reaches the hundreds.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<DevWorkflowRuleSetSnapshot>> ListEnabledRuleSetsAsync(CancellationToken cancellationToken = default);

    Task<DevWorkflowRunSnapshot> StartRunAsync(StartDevWorkflowRunCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevWorkflowRunSnapshot>> ListRunsAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The run list, newest first, with each run's definition name and node counters. Two queries whatever the row
    ///     count — the page, then one grouped pass over the node-runs of the listed runs — never one per row.
    ///     <para>
    ///         Both filters are optional: the work-item detail passes an id to embed that item's runs, and the run list
    ///         page passes a status. <see cref="ListRunsAsync" /> answers the same rows without the joins, for the
    ///         dispatcher's sweep, which needs neither name nor counters.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<DevWorkflowRunSummary>> ListRunSummariesAsync(Guid? workItemId = null,
        DevWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> TransitionRunAsync(TransitionDevWorkflowRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The node-runs a restart has to judge: everything left <c>Queued</c> or <c>Running</c>, read without writing
    ///     anything. The caller decides what each one costs — an attempt, a human, nothing — and hands those decisions
    ///     back to <see cref="ReconcileNonTerminalNodeRunsAsync" />, which is where they are committed.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Restart recovery, as ONE transaction. Runs auto-resume, so no run-level status moves; only node-runs the host
    ///     left <c>Queued</c> or <c>Running</c> collapse back to <c>Pending</c> so the dispatcher can re-admit them, each
    ///     with one <c>node.interrupted</c> event. <c>WaitingForApproval</c> and <c>Blocked</c> are durable human-wait
    ///     states and survive untouched. Idempotent by construction: a second pass finds none of those states and returns
    ///     empty.
    ///     <para>
    ///         <paramref name="verdicts" /> carry the caller's per-row decisions — an attempt spent, a human needed —
    ///         applied IN ORDER inside the same transaction as the collapse, because a recovery that commits the collapse
    ///         alone is one the next boot cannot finish: those rows read as ordinary <c>Pending</c> and would be re-run
    ///         with no attempt or budget accounting at all. Committing both together makes recovery all-or-nothing, so
    ///         any number of crashes during startup still repairs every interrupted node-run exactly once.
    ///     </para>
    ///     <para>
    ///         ONLY the rows whose live state still matches their verdict are collapsed. A stranded row with no verdict,
    ///         or one whose status, attempt or work session moved since the verdict was decided, is left untouched for
    ///         the caller's next pass — this is what makes the method safe against a writer the caller did not expect,
    ///         such as a second process sharing the database. Repairs run under
    ///         <see cref="DevWorkflowVersions.Any" />: the run's version has by then moved by one event per collapsed
    ///         row, and the per-row match is the check that matters here.
    ///     </para>
    ///     <para>
    ///         A non-null <paramref name="unjudged" /> makes this the caller's LAST pass: the rows it could not judge are
    ///         blocked for a human rather than left, decided against the live row inside this transaction and so immune
    ///         to the drift that stranded them in the first place. Pass it when walking away is worse than a human wait
    ///         — which it is at startup, because nothing downstream picks a stranded row up again.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<DevWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<DevWorkflowNodeRunVerdict> verdicts,
        DevWorkflowUnjudgedNodeRunBlock? unjudged = null,
        CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> MaterializeNodeRunsAsync(MaterializeDevWorkflowNodesCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> TransitionNodeRunAsync(TransitionDevWorkflowNodeRunCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A cross-node retry route, as ONE transaction: the <c>node.retry.routed</c> event and every node-run reset it
    ///     implies commit together or not at all.
    ///     <para>
    ///         Not a loop of <see cref="TransitionNodeRunAsync" /> calls, and the difference is a correctness one. A
    ///         route resets the whole subtree under the node it re-runs; a crash part-way through that loop left some
    ///         of those rows <c>Pending</c> while the rest kept the answers the re-run is about to invalidate — an
    ///         already-executed apply, an already-answered gate. Nothing reconciles that afterwards: startup recovery
    ///         only judges rows left <c>Queued</c> or <c>Running</c>, so a <c>Pending</c> row under <c>Succeeded</c>
    ///         ancestors is re-dispatched as if fresh and the run completes on the stale evidence beside it. All or
    ///         nothing means a crash leaves either the failure still recorded, which the dispatcher re-derives and
    ///         re-routes on its next sweep, or the fully reset subtree.
    ///     </para>
    ///     <para>
    ///         The operation id on <c>Route</c> is the whole command's: a replay answers the recorded result and writes
    ///         nothing. Quiescing the live lane work the resets supersede is the CALLER's, and belongs before this —
    ///         stopping a session is not something a transaction can roll back.
    ///     </para>
    /// </summary>
    Task<DevWorkflowMutationResult> RouteRetryAsync(RouteDevWorkflowRetryCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> AttachWorkSessionAsync(AttachDevWorkflowWorkSessionCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Takes no <c>sinceSequence</c>: a node-run's sequence is its insert order, not a change watermark, so a
    ///     status-only change would be invisible to such a feed. Status changes are observed through the event log.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<DevWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid nodeRunId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every work session a node run currently owns, across all runs — the set a workflow-kind session must belong
    ///     to in order to be reachable at all.
    ///     <para>
    ///         One distinct-projection query, for the startup sweep that deletes the sessions nothing points at: a
    ///         session created for a node run whose attach never committed is invisible to a work-item delete and
    ///         refused to every external caller, so this is the only thing that can find it.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<Guid>> ListOwnedWorkSessionIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The run driving a Development task, or null for a task no workflow owns — the reverse of the pointer a
    ///     <c>DevTask</c> node run stamps, read over that column's own index.
    ///     <para>
    ///         CONTRACT: the LATEST such node run answers. A task can be named by more than one node run over its life
    ///         (a re-run of the same definition drives the same task), and the question this exists to answer — where
    ///         does the approval for this task live NOW — has exactly one useful answer.
    ///     </para>
    /// </summary>
    Task<Guid?> FindRunIdForDevelopmentTaskAsync(Guid developmentTaskId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The same question for a whole task list, in ONE query. The project page asks it once per task otherwise,
    ///     which is a round trip per row on the one read that always has every row.
    ///     <para>
    ///         Same CONTRACT as the single-task read: the latest node run naming a task answers for it, and a task no
    ///         workflow owns is simply absent from the dictionary.
    ///     </para>
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> FindRunIdsForDevelopmentTasksAsync(IReadOnlyList<Guid> developmentTaskIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records an artifact, resolving its lineage by <c>(run, producing node key, name)</c>: the same node key
    ///     appending again versions the same lineage, and materialized siblings under one template get distinct ones.
    ///     Deleting the superseded version's bytes is the caller's job.
    /// </summary>
    Task<DevWorkflowMutationResult> AppendArtifactAsync(AppendDevWorkflowArtifactCommand command, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> RecordArtifactUsesAsync(RecordDevWorkflowArtifactUsesCommand command, CancellationToken cancellationToken = default);

    /// <summary>Pure DB work over the recorded uses; it flags dependents and never regenerates anything.</summary>
    Task<DevWorkflowMutationResult> MarkDependentsStaleAsync(MarkDevWorkflowStaleCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The artifact cursor is append-correct only: an artifact's sequence is allocated at insert and never
    ///     re-stamped, so a <c>sinceSequence</c> page carries every artifact that has appeared since and no staleness
    ///     flip that has happened since. Staleness mutations are announced on the event feed as
    ///     <c>artifact.stale.marked</c> and observed by refetching the artifact, never by advancing this cursor.
    /// </summary>
    Task<IReadOnlyList<DevWorkflowArtifactSnapshot>> ListArtifactsAsync(Guid runId, long sinceSequence = 0, CancellationToken cancellationToken = default);

    Task<DevWorkflowArtifactSnapshot> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListConsumedArtifactIdsAsync(Guid nodeRunId, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> RecordDecisionAsync(RecordDevWorkflowDecisionCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevWorkflowDecisionSnapshot>> ListDecisionsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The idempotent-replay read for a decision. A repeated POST has to return the same <em>body</em>, not just the
    ///     same run state, and the mutation result carries no decision id, subject or decided-at.
    /// </summary>
    Task<DevWorkflowDecisionSnapshot?> FindDecisionByOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The <c>event_type</c> this operation has already committed against this run, or <see langword="null" /> when
    ///     it has not run.
    ///     <para>
    ///         Every mutation resolves the same fact internally, so a replayed command is safe wherever it lands. It is
    ///         exposed because a caller has to ask BEFORE judging legality: a command that committed and was then
    ///         retried is a replay, and re-checking the status it has already changed would answer a conflict to a
    ///         caller who did nothing wrong.
    ///     </para>
    ///     <para>
    ///         It answers the event TYPE rather than merely "yes", because an operation id names one ACT and not one
    ///         run: a caller that reuses a pause's id on a cancel is replaying nothing, and a bare yes would report
    ///         that cancel as done without anything having been cancelled. The caller compares what was recorded
    ///         against the verb it is serving.
    ///     </para>
    ///     <para>
    ///         Deliberately not the recorded <see cref="DevWorkflowMutationResult" />: a read handing back a mutation's
    ///         result is indistinguishable — to a reader, and to the reflection that holds the publishing decorator to
    ///         every mutation this interface declares — from having committed one.
    ///     </para>
    /// </summary>
    Task<string?> FindOperationEventTypeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default);

    Task<DevWorkflowMutationResult> AppendEventAsync(AppendDevWorkflowEventCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DevWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long sinceSequence = 0,
        int limit = 200,
        CancellationToken cancellationToken = default);
}

public sealed class DevWorkflowConcurrencyException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

public sealed class DevWorkflowInvalidTransitionException(string message) : InvalidOperationException(message);

public sealed class DevWorkflowNotFoundException(string message) : InvalidOperationException(message);

/// <summary>
///     A work item that already has a live run was asked for a second one, or asked to be deleted. Its own conflict
///     type rather than an invalid transition, because the answer is different: wait for the run, or cancel it.
/// </summary>
public sealed class DevWorkflowRunInFlightException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

/// <summary>
///     A second human act on a gate that is already answered — a NEW operation id arriving at a decided node-run,
///     which is not the idempotent replay a repeated one is.
///     <para>
///         <see cref="StandingDecision" /> travels with it so the API can tell the operator WHAT was decided instead
///         of only that their click failed.
///     </para>
/// </summary>
public sealed class DevWorkflowGateAlreadyDecidedException(string message, DevWorkflowDecisionKind standingDecision) : InvalidOperationException(message)
{
    public DevWorkflowDecisionKind StandingDecision { get; } = standingDecision;
}
