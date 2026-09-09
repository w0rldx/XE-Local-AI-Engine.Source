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

    // The cost-telemetry columns, trailing and optional so a caller composing a node run by hand — a test, a fake
    // store — keeps compiling and reads back exactly what a row written before this slice reads back: nulls.
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
    int? WorkSessionSteps = null,
    // Trailing: the thirteenth cost column, how much of AgentTurnMs was a local runtime warming (see the entity).
    long? ModelReadinessMs = null,
    // Trailing: the fourteenth and fifteenth, what the box looked like when the serving model was last loaded. A warm
    // run reports the EARLIER load's figures; ModelReadinessMs tells the reader which it is (see the entity).
    long? VramFreeAtLoadBytes = null,
    long? VramAdmittedBytes = null);

/// <summary>
///     What one node-run attempt spent and where it routed, collected at the terminal-or-blocked transition and applied
///     to the row by <see cref="IDevWorkflowStore.TransitionNodeRunAsync" />. Every member is optional: a member left
///     null leaves its column untouched, which is how a node run with no work session and no development task still
///     records a route beside fifteen nulls.
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
    int? WorkSessionSteps = null,
    // How much of AgentTurnMs was a local runtime warming rather than generating; null when none of the attempt's
    // turns warmed one. Trailing for the same positional-record reason as the twelve above it.
    long? ModelReadinessMs = null,
    // The VRAM the box had free, and the VRAM admission reserved, at the most recent successful load of the model that
    // served this run. Observational only, and possibly an EARLIER load than this run (see the entity).
    long? VramFreeAtLoadBytes = null,
    long? VramAdmittedBytes = null);

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
/// <param name="MaxAttempts">
///     The row's OWN per-node cap, projected here so restart recovery can refuse to increment a row past it. The live
///     path already checks it before every automatic re-attempt; recovery bypassed that check entirely and reset an
///     interrupted row at its cap to <c>Pending</c> with one more attempt than it declares (FU3-4). Defaulted to
///     <see cref="int.MaxValue" /> — "no cap" — so a projection that forgets it admits, which is what recovery did
///     before, rather than blocking every row it sees.
/// </param>
public sealed record DevWorkflowReconciledNodeRun(
    Guid NodeRunId,
    Guid RunId,
    string NodeKey,
    DevWorkflowNodeType NodeType,
    DevWorkflowNodeRunStatus Status,
    int Attempt,
    Guid? WorkSessionId,
    int MaxAttempts = int.MaxValue);

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

/// <summary>
///     One node-run to create. <see cref="InputJson" /> on an entry node is what carries the operator's request to the
///     first agent.
///     <para>
///         <see cref="Status" /> and <see cref="OutputJson" /> are the two trailing members that let a seed land
///         ALREADY TERMINAL, which the zero-task decomposition needs: a <c>Pending</c> row at a template key is
///         admissible — its only inbound edge is dropped as template-sourced, leaving no edge states at all — so the
///         tool lane would really run the template's validation commands, and a crash between a create and a follow-up
///         transition would do exactly that. Omitting both is today's behaviour for every other caller.
///     </para>
///     <para>
///         The store refuses both halves of the same rule: a seed lands <c>Pending</c> or terminal and nothing between,
///         because a live status is a lane's claim on a row no lane has taken; and only a terminal seed may carry an
///         output document, because that document says what the row PRODUCED.
///     </para>
/// </summary>
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
    int? MaterializationIndex = null,
    DevWorkflowNodeRunStatus Status = DevWorkflowNodeRunStatus.Pending,
    string? OutputJson = null);

/// <summary>
///     Creates node-runs on a run. A non-null <see cref="GraphJson" /> also rewrites the run's pinned graph and bumps
///     its revision in the same transaction — the dynamic-expansion path, recorded as <c>graph.changed</c>. A null one
///     is the initial materialization at run start, where the graph is already pinned and unchanged.
///     <para>
///         <see cref="RouteJson" /> re-records the route of the node run that PRODUCED this expansion, against the
///         rewritten graph and inside the same transaction. Its route was computed when it settled, which was before
///         the clone-root edges existed — so left alone it would list the authored join edge and omit every root the
///         next tick actually admits. Both members are set together or not at all; a route needs a row to land on.
///     </para>
/// </summary>
public sealed record MaterializeDevWorkflowNodesCommand(
    Guid RunId,
    long ExpectedVersion,
    Guid OperationId,
    IReadOnlyList<DevWorkflowNodeRunSeed> NodeRuns,
    string? GraphJson = null,
    Guid? RouteNodeRunId = null,
    string? RouteJson = null);

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
    DevWorkflowNodeTelemetry? Telemetry = null,
    /// <summary>
    ///     Buys the node run exactly one more attempt, by raising the cap its own row carries. Set only by an
    ///     operator's <c>Retry</c>, which is allowed AT the cap and widens it by one each time it is used — so
    ///     <c>Attempt</c> never exceeds the row's own <c>MaxAttempts</c>, and the retry policy's cap check goes on
    ///     meaning what it says instead of the run reporting that it broke its own budget.
    /// </summary>
    bool WidenMaxAttempts = false,
    /// <summary>
    ///     The run-wide re-attempt budget this move has to fit inside, admitted INSIDE the mutation's own transaction —
    ///     the same field, and the same reason, as <see cref="RecordDevWorkflowDecisionCommand.MaxTotalAttempts" />.
    ///     Set only by an automatic re-attempt; <see langword="null" /> means no budget applies, which is every other
    ///     transition.
    ///     <para>
    ///         Carried at all because the automatic path checked the budget on a read taken before its write and then
    ///         committed with no re-check: a human <c>Retry</c> committing in that window spent the same last slot, and
    ///         the run made one more re-attempt than it allows (FU3-4). Only a count taken under the writer lock
    ///         refuses the second.
    ///     </para>
    /// </summary>
    int? MaxTotalAttempts = null);

/// <summary>
///     One cross-node retry route, as the single decision it is: the <c>node.retry.routed</c> event that records it and
///     every node-run reset that decision implies.
/// </summary>
/// <param name="Route">The routing event. Its run, expected version and operation id govern the whole command.</param>
/// <param name="Resets">
///     The node-run moves the route implies, applied IN ORDER after the event. Each must name <c>Route.RunId</c>.
/// </param>
/// <param name="MaxTotalAttempts">
///     The run-wide re-attempt budget the WHOLE cascade has to fit inside, admitted inside this command's transaction.
///     Top-level rather than per reset because a route's cost is <paramref name="Resets" />.Count — admitting a fan-out one
///     attempt at a time is how a run overspends its budget by the width of its graph. <see langword="null" /> means no
///     budget applies.
/// </param>
public sealed record RouteDevWorkflowRetryCommand(
    AppendDevWorkflowEventCommand Route,
    IReadOnlyList<TransitionDevWorkflowNodeRunCommand> Resets,
    int? MaxTotalAttempts = null);

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
    int? MaxTotalAttempts = null,
    /// <summary>
    ///     The attempt the caller VALIDATED this decision against, re-checked inside the recording transaction.
    ///     <c>ExpectedVersion</c> is <c>Any</c> here — an answer is about a node run rather than about the run's
    ///     sequence — so this pair is the only thing between a decision and a routed reset that moved the row after
    ///     the caller read it: the answer would otherwise be stamped with whatever attempt the reset left behind,
    ///     orphaned on a fresh <c>Pending</c> try nobody was asked about, or recorded against the old attempt and
    ///     later counted by the run composer's <c>operatorRetries</c> for a widening that never happened.
    ///     <c>null</c> skips the check, for a caller with nothing to compare.
    /// </summary>
    int? ExpectedAttempt = null,
    /// <summary>The status the caller validated against, checked with <see cref="ExpectedAttempt" />.</summary>
    DevWorkflowNodeRunStatus? ExpectedStatus = null);

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
