namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The sentinel <see cref="Any" /> version.
/// </summary>
/// <remarks>
///     The dispatcher moves run and node-run status while a human HTTP action may be writing a decision on the same
///     run, so a status move — which has no lost update to protect against — passes <see cref="Any" /> and never
///     loses the race to a content write.
/// </remarks>
public static class DevWorkflowVersions
{
    public const long Any = -1;
}

/// <summary>
///     The node-status tallies a work-item or run summary carries, so a list page can draw progress without a per-row
///     query. <see cref="BlockingGateNodeRunId" /> is the first node-run waiting on a human, in sequence order.
/// </summary>
public sealed class DevWorkflowNodeCounters
{
    public required int Queued { get; init; }

    public required int Running { get; init; }

    public required int Completed { get; init; }

    public required int Total { get; init; }

    public required int PendingDecisionCount { get; init; }

    public required Guid? BlockingGateNodeRunId { get; init; }

    public static DevWorkflowNodeCounters Empty { get; } = new()
    {
        Queued = 0,
        Running = 0,
        Completed = 0,
        Total = 0,
        PendingDecisionCount = 0,
        BlockingGateNodeRunId = null
    };
}

public sealed class DevWorkflowWorkItemSnapshot
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Request { get; init; }

    public required DevWorkflowWorkItemStatus Status { get; init; }

    public required Guid? DevelopmentProjectId { get; init; }

    public required Guid? LatestRunId { get; init; }

    public required DevWorkflowRunStatus? LatestRunStatus { get; init; }

    public required string? LatestRunDefinitionName { get; init; }

    public required DevWorkflowNodeCounters LatestRunNodes { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }
}

/// <summary>What work-item detail embeds as its run list: a run without the graph blob, plus its node counters.</summary>
public sealed class DevWorkflowRunSummary
{
    public required Guid Id { get; init; }

    public required Guid WorkItemId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required string? DefinitionName { get; init; }

    public required DevWorkflowRunStatus Status { get; init; }

    public required DevWorkflowNodeCounters Nodes { get; init; }

    public required string? FailureClass { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? EndedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed record DevWorkflowRunSnapshot
{
    public required Guid Id { get; init; }

    public required Guid WorkItemId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required string DefinitionGraphHash { get; init; }

    public required string GraphJson { get; init; }

    public required int GraphRevision { get; init; }

    public required DevWorkflowRunStatus Status { get; init; }

    public required long LastSequence { get; init; }

    public required string? FailureClass { get; init; }

    public required string? TerminalReason { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? EndedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }
}

/// <summary>What the definition list returns: no graph blob, so listing never decrypts one.</summary>
public sealed class DevWorkflowDefinitionSummary
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string GraphHash { get; init; }

    public required int NodeCount { get; init; }

    public required DevWorkflowDefinitionSource Source { get; init; }

    public required string? SeedSlug { get; init; }

    public required bool Archived { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class DevWorkflowDefinitionSnapshot
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string GraphJson { get; init; }

    public required string GraphHash { get; init; }

    public required int NodeCount { get; init; }

    public required DevWorkflowDefinitionSource Source { get; init; }

    public required string? SeedSlug { get; init; }

    public required bool Archived { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     One rule set in full, body included. <see cref="ContentSha256" /> is computed store-side alongside the body, so
///     the hash and the text can never describe different documents.
/// </summary>
public sealed record DevWorkflowRuleSetSnapshot
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string ScopeJson { get; init; }

    public required bool Enabled { get; init; }

    public required string Body { get; init; }

    public required string ContentSha256 { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     A rule set WITHOUT its body — everything the list page draws and everything the resolver matches on. The body is
///     the one encrypted column here, so a feed that never asks for it never decrypts one.
/// </summary>
public sealed class DevWorkflowRuleSetSummary
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string ScopeJson { get; init; }

    public required bool Enabled { get; init; }

    public required string ContentSha256 { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>The canonical node-run field set. <see cref="WorkSessionAvailable" /> is read from the other family, never stored.</summary>
public sealed record DevWorkflowNodeRunSnapshot
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required string NodeKey { get; init; }

    public required DevWorkflowNodeType NodeType { get; init; }

    public required int Attempt { get; init; }

    public required int MaxAttempts { get; init; }

    public required int SessionResumes { get; init; }

    public required DevWorkflowNodeRunStatus Status { get; init; }

    public required string? QueueReason { get; init; }

    public required DevWorkflowDecisionKind? PendingDecisionKind { get; init; }

    public required long Sequence { get; init; }

    public required Guid? WorkSessionId { get; init; }

    public required bool WorkSessionAvailable { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required Guid? DevelopmentProjectId { get; init; }

    public required Guid? DevelopmentTaskId { get; init; }

    public required string? InputJson { get; init; }

    public required string? OutputJson { get; init; }

    public required string? PolicyResolutionJson { get; init; }

    public required Guid? MaterializedFromNodeRunId { get; init; }

    public required int? MaterializationIndex { get; init; }

    public required string? FailureClass { get; init; }

    public required string? TerminalReason { get; init; }

    public required long? QueuedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? EndedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }

    // The cost-telemetry columns, trailing and optional so a caller composing a node run by hand — a test, a fake
    // store — keeps compiling and reads back exactly what a row written before this slice reads back: nulls.
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? ReasoningTokens { get; init; }

    public long? EstimatedInputTokens { get; init; }

    public int? ProviderCalls { get; init; }

    public int? ToolCalls { get; init; }

    public long? ToolSchemaTokens { get; init; }

    public string? ToolNamesJson { get; init; }

    public long? AgentTurnMs { get; init; }

    public string? ServedModelName { get; init; }

    public string? RouteJson { get; init; }

    public int? WorkSessionSteps { get; init; }

    // Trailing: the thirteenth cost column, how much of AgentTurnMs was a local runtime warming (see the entity).
    public long? ModelReadinessMs { get; init; }

    // Trailing: the fourteenth and fifteenth, what the box looked like when the serving model was last loaded. A warm
    // run reports the EARLIER load's figures; ModelReadinessMs tells the reader which it is (see the entity).
    public long? VramFreeAtLoadBytes { get; init; }

    public long? VramAdmittedBytes { get; init; }
}

/// <summary>
///     What one node-run attempt spent and where it routed, collected at the terminal-or-blocked transition and
///     applied to the row by <see cref="IDevWorkflowStore.TransitionNodeRunAsync" />.
/// </summary>
/// <remarks>
///     Every member is optional: a member left null leaves its column untouched, which is how a node run with no work
///     session and no development task still records a route beside fifteen nulls. Metadata only — counts, a served
///     model name, structural node keys and tool NAMES; no prompt, tool argument, tool result or transcript may ever
///     be added here. <see cref="RouteJson" /> and <see cref="ToolNamesJson" /> arrive already serialized because this
///     record crosses into persistence, where neither the graph projection nor the collector's name set is visible.
/// </remarks>
public sealed record DevWorkflowNodeTelemetry
{
    public long? InputTokens { get; init; }

    public long? OutputTokens { get; init; }

    public long? ReasoningTokens { get; init; }

    public long? EstimatedInputTokens { get; init; }

    public int? ProviderCalls { get; init; }

    public int? ToolCalls { get; init; }

    public long? ToolSchemaTokens { get; init; }

    public string? ToolNamesJson { get; init; }

    public long? AgentTurnMs { get; init; }

    public string? ServedModelName { get; init; }

    public string? RouteJson { get; init; }

    public int? WorkSessionSteps { get; init; }

    // How much of AgentTurnMs was a local runtime warming rather than generating; null when none of the attempt's
    // turns warmed one. Trailing for the same positional-record reason as the twelve above it.
    public long? ModelReadinessMs { get; init; }

    // The VRAM the box had free, and the VRAM admission reserved, at the most recent successful load of the model that
    // served this run. Observational only, and possibly an EARLIER load than this run (see the entity).
    public long? VramFreeAtLoadBytes { get; init; }

    public long? VramAdmittedBytes { get; init; }
}

public sealed class DevWorkflowRunEventSnapshot
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required Guid? NodeRunId { get; init; }

    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    public required string? DetailJson { get; init; }

    public required Guid? OperationId { get; init; }

    public required string? Outcome { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary><see cref="IsLatest" /> is computed (max version per lineage) and ships on the wire, so no client derives it.</summary>
public sealed record DevWorkflowArtifactSnapshot
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required Guid LineageId { get; init; }

    public required string ProducingNodeKey { get; init; }

    public required Guid ProducedByNodeRunId { get; init; }

    public required string Name { get; init; }

    public required int Version { get; init; }

    public required bool IsLatest { get; init; }

    public required DevWorkflowArtifactKind Kind { get; init; }

    public required string MediaType { get; init; }

    public required string ContentSha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required bool IsValid { get; init; }

    public required bool IsStale { get; init; }

    public required long? StaleSinceSequence { get; init; }

    public required Guid? StaleBecauseArtifactId { get; init; }

    public required string? StaleReason { get; init; }

    public required string ManagedReference { get; init; }

    public required long Sequence { get; init; }

    public required long CreatedAtUtc { get; init; }
}

public sealed class DevWorkflowDecisionSnapshot
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required int Attempt { get; init; }

    public required DevWorkflowDecisionKind Decision { get; init; }

    public required string? Comment { get; init; }

    public required string? PayloadJson { get; init; }

    public required string? DecidedBySubject { get; init; }

    public required Guid OperationId { get; init; }

    public required long Sequence { get; init; }

    public required long DecidedAtUtc { get; init; }
}

/// <summary>
///     One row per node-run the host left mid-flight, carrying enough detail for the runtime to rebuild its dispatch
///     table without a follow-up read per row.
/// </summary>
/// <remarks>
///     <see cref="Status" /> is the status the node-run held <em>before</em> the collapse — what it was doing is the
///     useful fact; where it landed is always <c>Pending</c>, unless a repair moved it further.
/// </remarks>
public sealed class DevWorkflowReconciledNodeRun
{
    public required Guid NodeRunId { get; init; }

    public required Guid RunId { get; init; }

    public required string NodeKey { get; init; }

    public required DevWorkflowNodeType NodeType { get; init; }

    public required DevWorkflowNodeRunStatus Status { get; init; }

    public required int Attempt { get; init; }

    public required Guid? WorkSessionId { get; init; }

    /// <summary>
    ///     The row's OWN per-node cap, projected here so restart recovery can refuse to increment a row past it.
    /// </summary>
    /// <remarks>
    ///     The live path checks it before every automatic re-attempt; recovery without it resets an interrupted row at
    ///     its cap to <c>Pending</c> with one more attempt than the row declares (FU3-4). Defaulted to
    ///     <see cref="int.MaxValue" /> — "no cap" — so a projection that forgets it admits rather than blocking every
    ///     row it sees.
    /// </remarks>
    public int MaxAttempts { get; init; } = int.MaxValue;
}

/// <summary>
///     One judged node-run: the row as the caller observed it, and what to do with it once the collapse has confirmed
///     it is still that row.
/// </summary>
/// <remarks>
///     The expectation is the whole point. A verdict is only true of the state it was decided from, so the collapse
///     matches each one against the live row and takes only the rows that still agree. A row that moved under the
///     caller — or became stranded after the caller read — is left exactly as it is: collapsing it unjudged would
///     strand it at <c>Pending</c>, where nothing would judge it again, and repairing it from stale evidence would
///     spend an attempt on a state it is no longer in.
/// </remarks>
public sealed class DevWorkflowNodeRunVerdict
{
    public required Guid NodeRunId { get; init; }

    public required DevWorkflowNodeRunStatus ObservedStatus { get; init; }

    public required int ObservedAttempt { get; init; }

    public required Guid? ObservedWorkSessionId { get; init; }

    public required IReadOnlyList<TransitionDevWorkflowNodeRunCommand> Repairs { get; init; }
}

/// <summary>
///     Turns a reconciliation into a SETTLING pass: every stranded node-run no verdict matched is blocked for a human
///     instead of being left as it is.
/// </summary>
/// <remarks>
///     The blocked state is this command's business rather than the caller's, because it is what the pass promises: a
///     settling pass leaves no node-run stranded, and a row that is neither dispatchable nor waiting on a person is
///     one nothing will ever pick up. The row lands <c>Blocked</c> with an <c>Abandon</c> decision pending and its
///     work item blocked with it — costing no attempt, the only honest price for a row nobody could judge.
/// </remarks>
public sealed class DevWorkflowUnjudgedNodeRunBlock
{
    public required string FailureClass { get; init; }

    public required string SanitizedReason { get; init; }
}

/// <summary>
///     What one mutation committed: the watermark it allocated for its event, and the run row's post-commit version,
///     status and graph revision.
/// </summary>
/// <remarks>
///     <see cref="SupersededArtifactId" /> is set only by <see cref="IDevWorkflowStore.AppendArtifactAsync" /> when
///     the write added a new version over an existing lineage. Its bytes are still on disk: the caller that owns the
///     blob store deletes them after the commit, because the schema project cannot reach the blob layer.
/// </remarks>
public sealed class DevWorkflowMutationResult
{
    public required Guid RunId { get; init; }

    public required long Sequence { get; init; }

    public required long Version { get; init; }

    public required DevWorkflowRunStatus Status { get; init; }

    public required int GraphRevision { get; init; }

    public Guid? SupersededArtifactId { get; init; }
}

/// <summary>
///     What a work-item delete removed, and what it could not: the work sessions its agent node runs owned and the
///     runs whose artifact bytes are still on disk.
/// </summary>
/// <remarks>
///     Answered by the delete rather than gathered before it, and that ordering is the point: the authoritative
///     live-run guard runs inside the same transaction, so a caller cannot destroy a transcript for a delete that is
///     then refused. It also makes the set complete by construction — a caller paging its own read would orphan
///     everything past the page.
/// </remarks>
public sealed class DevWorkflowWorkItemDeletion
{
    public required int RemovedRows { get; init; }

    public required IReadOnlyList<Guid> RunIds { get; init; }

    public required IReadOnlyList<Guid> WorkSessionIds { get; init; }
}

public sealed class CreateDevWorkflowWorkItemCommand
{
    public required Guid WorkItemId { get; init; }

    public required string Title { get; init; }

    public required string Request { get; init; }

    public Guid? DevelopmentProjectId { get; init; }
}

public sealed class UpdateDevWorkflowWorkItemCommand
{
    public required Guid WorkItemId { get; init; }

    public required long ExpectedVersion { get; init; }

    public string? Title { get; init; }

    public string? Request { get; init; }

    public Guid? DevelopmentProjectId { get; init; }
}

public sealed class CreateDevWorkflowDefinitionCommand
{
    public required Guid DefinitionId { get; init; }

    public required string Name { get; init; }

    public required string GraphJson { get; init; }

    public required int NodeCount { get; init; }

    public DevWorkflowDefinitionSource Source { get; init; }

    public string? SeedSlug { get; init; }
}

public sealed class UpdateDevWorkflowDefinitionCommand
{
    public required Guid DefinitionId { get; init; }

    public required int ExpectedVersion { get; init; }

    public string? Name { get; init; }

    public string? GraphJson { get; init; }

    public int? NodeCount { get; init; }
}

public sealed class CreateDevWorkflowRuleSetCommand
{
    public required Guid RuleSetId { get; init; }

    public required string Name { get; init; }

    public required string Body { get; init; }

    public required string ScopeJson { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
///     A whole replacement, not a patch: the rule set is a document an operator edits as one, and a partial update
///     would have to invent a spelling for "clear the description" that a PUT body already has.
/// </summary>
public sealed class UpdateDevWorkflowRuleSetCommand
{
    public required Guid RuleSetId { get; init; }

    public required int ExpectedVersion { get; init; }

    public required string Name { get; init; }

    public required string Body { get; init; }

    public required string ScopeJson { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
///     Starts a run. <see cref="NodeRuns" /> is the run's whole initial node set, created in the SAME transaction as
///     the run row.
/// </summary>
/// <remarks>
///     A run that committed without them could only be repaired by re-deriving the seeds, and the caller's per-run
///     inputs — which live nowhere but the entry rows — would be gone by then. Empty is legal and means "rows only",
///     which is what a store-level test wants; no runtime path uses it, and a run with no node runs is one nothing
///     will ever advance.
/// </remarks>
public sealed class StartDevWorkflowRunCommand
{
    public required Guid RunId { get; init; }

    public required Guid WorkItemId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required string DefinitionGraphHash { get; init; }

    public required string GraphJson { get; init; }

    public IReadOnlyList<DevWorkflowNodeRunSeed>? NodeRuns { get; init; }
}

/// <summary>
///     A run status move. <see cref="WorkItemStatus" /> lets the runtime write the work item's status inside the same
///     transaction that transitions the run, which is the only way the two can never disagree.
/// </summary>
public sealed class TransitionDevWorkflowRunCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required DevWorkflowRunStatus TargetStatus { get; init; }

    public Guid? OperationId { get; init; }

    public string? FailureClass { get; init; }

    public string? SanitizedReason { get; init; }

    public DevWorkflowWorkItemStatus? WorkItemStatus { get; init; }
}

/// <summary>
///     One node-run to create. <see cref="InputJson" /> on an entry node is what carries the operator's request to the
///     first agent.
/// </summary>
/// <remarks>
///     <see cref="Status" /> and <see cref="OutputJson" /> let a seed land ALREADY TERMINAL, which the zero-task
///     decomposition needs: a <c>Pending</c> row at a template key is admissible — its only inbound edge is dropped as
///     template-sourced, leaving no edge states — so the tool lane would run the template's validation commands. The
///     store refuses both halves of one rule: a seed lands <c>Pending</c> or terminal, never between, because a live
///     status claims a row no lane has taken; and only a terminal seed may carry an output, which says what it PRODUCED.
/// </remarks>
public sealed class DevWorkflowNodeRunSeed
{
    public required Guid NodeRunId { get; init; }

    public required string NodeKey { get; init; }

    public required DevWorkflowNodeType NodeType { get; init; }

    public int MaxAttempts { get; init; } = 1;

    public Guid? AgentDefinitionId { get; init; }

    public Guid? DevelopmentProjectId { get; init; }

    public string? InputJson { get; init; }

    public string? PolicyResolutionJson { get; init; }

    public Guid? MaterializedFromNodeRunId { get; init; }

    public int? MaterializationIndex { get; init; }

    public DevWorkflowNodeRunStatus Status { get; init; }

    public string? OutputJson { get; init; }
}

/// <summary>
///     Creates node-runs on a run.
/// </summary>
/// <remarks>
///     A non-null <see cref="GraphJson" /> also rewrites the run's pinned graph and bumps its revision in the same
///     transaction — the dynamic-expansion path, recorded as <c>graph.changed</c>; a null one is the initial
///     materialization, where the graph is already pinned. <see cref="RouteJson" /> re-records the route of the node
///     run that PRODUCED the expansion: computed before the clone-root edges existed, it would otherwise list the
///     authored join edge and omit every root the next tick admits. Both are set together; a route needs a row.
/// </remarks>
public sealed class MaterializeDevWorkflowNodesCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required IReadOnlyList<DevWorkflowNodeRunSeed> NodeRuns { get; init; }

    public string? GraphJson { get; init; }

    public Guid? RouteNodeRunId { get; init; }

    public string? RouteJson { get; init; }
}

/// <summary>
///     A node-run status move. <see cref="IncrementAttempt" /> is the retry-in-place path; the row is never duplicated.
/// </summary>
/// <remarks>
///     <see cref="Outcome" /> overrides the token derived from <see cref="TargetStatus" /> for what the status alone
///     cannot express (<c>timeout</c>, <c>interrupted</c>, <c>rejected</c>, <c>changes-requested</c>).
///     <see cref="ClearWorkSession" /> and <see cref="InputJson" />, and why <see cref="DetailJson" /> exists for the
///     one move whose evidence is not on the row afterwards: docs/wiki/08-data-and-persistence.md ("Dev-workflow
///     node-run transitions").
/// </remarks>
public sealed record TransitionDevWorkflowNodeRunCommand
{
    public required Guid RunId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required DevWorkflowNodeRunStatus TargetStatus { get; init; }

    public Guid? OperationId { get; init; }

    public string? QueueReason { get; init; }

    public DevWorkflowDecisionKind? PendingDecisionKind { get; init; }

    public string? OutputJson { get; init; }

    public string? InputJson { get; init; }

    public string? FailureClass { get; init; }

    public string? TerminalReason { get; init; }

    public string? DetailJson { get; init; }

    public Guid? DevelopmentTaskId { get; init; }

    public bool IncrementAttempt { get; init; }

    public bool ClearWorkSession { get; init; }

    public string? Outcome { get; init; }

    public DevWorkflowWorkItemStatus? WorkItemStatus { get; init; }

    // What the attempt this move settles cost. Set by the publishing decorator on a terminal, Blocked or
    // WaitingForApproval move and by nothing else, so no call site has to remember it.
    public DevWorkflowNodeTelemetry? Telemetry { get; init; }

    /// <summary>
    ///     Buys the node run exactly one more attempt, by raising the cap its own row carries.
    /// </summary>
    /// <remarks>
    ///     Set only by an operator's <c>Retry</c>, which is allowed AT the cap and widens it by one each time it is
    ///     used — so <c>Attempt</c> never exceeds the row's own <c>MaxAttempts</c>, and the retry policy's cap check
    ///     goes on meaning what it says instead of the run reporting that it broke its own budget.
    /// </remarks>
    public bool WidenMaxAttempts { get; init; }

    /// <summary>
    ///     The run-wide re-attempt budget this move has to fit inside, admitted INSIDE the mutation's own transaction —
    ///     the same field, and the same reason, as <see cref="RecordDevWorkflowDecisionCommand.MaxTotalAttempts" />.
    /// </summary>
    /// <remarks>
    ///     Set only by an automatic re-attempt; <see langword="null" /> means no budget applies, which is every other
    ///     transition. It is carried at all because a budget checked on a read taken before the write and committed
    ///     with no re-check lets a human <c>Retry</c> committing in that window spend the same last slot, and the run
    ///     makes one more re-attempt than it allows (FU3-4). Only a count taken under the writer lock refuses the
    ///     second.
    /// </remarks>
    public int? MaxTotalAttempts { get; init; }
}

/// <summary>
///     One cross-node retry route, as the single decision it is: the <c>node.retry.routed</c> event that records it and
///     every node-run reset that decision implies.
/// </summary>
public sealed record RouteDevWorkflowRetryCommand
{
    /// <summary>The routing event. Its run, expected version and operation id govern the whole command.</summary>
    public required AppendDevWorkflowEventCommand Route { get; init; }

    /// <summary>The node-run moves the route implies, applied IN ORDER after the event. Each must name <c>Route.RunId</c>.</summary>
    public required IReadOnlyList<TransitionDevWorkflowNodeRunCommand> Resets { get; init; }

    /// <summary>
    ///     The run-wide re-attempt budget the WHOLE cascade has to fit inside, admitted inside this command's
    ///     transaction. <see langword="null" /> means no budget applies.
    /// </summary>
    /// <remarks>
    ///     Top-level rather than per reset because a route's cost is <see cref="Resets" />.Count — admitting a fan-out
    ///     one attempt at a time is how a run overspends its budget by the width of its graph.
    /// </remarks>
    public int? MaxTotalAttempts { get; init; }
}

public sealed class AttachDevWorkflowWorkSessionCommand
{
    public required Guid RunId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid WorkSessionId { get; init; }

    public Guid? OperationId { get; init; }

    public bool CountsAsResume { get; init; }
}

public sealed class AppendDevWorkflowArtifactCommand
{
    public required Guid RunId { get; init; }

    public required Guid ArtifactId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required DevWorkflowArtifactKind Kind { get; init; }

    public required string Name { get; init; }

    public required string MediaType { get; init; }

    public required string ContentSha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required string ManagedReference { get; init; }
}

public sealed class RecordDevWorkflowArtifactUsesCommand
{
    public required Guid RunId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required IReadOnlyList<Guid> ArtifactIds { get; init; }
}

public sealed class MarkDevWorkflowStaleCommand
{
    public required Guid RunId { get; init; }

    public required Guid SupersededArtifactId { get; init; }

    public required Guid SupersedingArtifactId { get; init; }

    public required long ExpectedVersion { get; init; }

    public Guid? OperationId { get; init; }

    public string StaleReason { get; init; } = DevWorkflowStaleReasons.SupersededInput;
}

/// <summary>
///     One human decision on one node-run attempt.
/// </summary>
/// <remarks>
///     <see cref="MaxTotalAttempts" /> is the run-wide re-attempt budget this act has to fit inside. It travels on the
///     command because the store reads no options, and is set only for <c>Retry</c> — the one decision that authorises
///     another attempt; null means no budget applies, which is every other decision. It is carried rather than left to
///     the caller because admission has to happen where the write does: several blocked node runs each checked before
///     the dispatcher settles any would all see the same unspent budget and all pass.
/// </remarks>
public sealed class RecordDevWorkflowDecisionCommand
{
    public required Guid RunId { get; init; }

    public required Guid DecisionId { get; init; }

    public required Guid NodeRunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required Guid OperationId { get; init; }

    public required DevWorkflowDecisionKind Decision { get; init; }

    public string? Comment { get; init; }

    public string? PayloadJson { get; init; }

    public string? DecidedBySubject { get; init; }

    public int? MaxTotalAttempts { get; init; }

    /// <summary>
    ///     The attempt the caller VALIDATED this decision against, re-checked inside the recording transaction;
    ///     <c>null</c> skips the check, for a caller with nothing to compare.
    /// </summary>
    /// <remarks>
    ///     <c>ExpectedVersion</c> is <c>Any</c> here — an answer is about a node run, not about the run's sequence —
    ///     so this pair is the only thing between a decision and a routed reset that moved the row after the caller
    ///     read it. The answer would otherwise be stamped with whatever attempt the reset left behind, orphaned on a
    ///     fresh <c>Pending</c> try nobody was asked about, or recorded against the old attempt and later counted by
    ///     the run composer's <c>operatorRetries</c> for a widening that never happened.
    /// </remarks>
    public int? ExpectedAttempt { get; init; }

    /// <summary>The status the caller validated against, checked with <see cref="ExpectedAttempt" />.</summary>
    public DevWorkflowNodeRunStatus? ExpectedStatus { get; init; }
}

public sealed record AppendDevWorkflowEventCommand
{
    public required Guid RunId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required string EventType { get; init; }

    public Guid? NodeRunId { get; init; }

    public Guid? OperationId { get; init; }

    public string? Outcome { get; init; }

    public string? DetailJson { get; init; }
}

/// <summary>The closed <c>stale_reason</c> token set. The <em>which</em> is <c>StaleBecauseArtifactId</c>, not this.</summary>
public static class DevWorkflowStaleReasons
{
    public const string SupersededInput = "superseded-input";
}

/// <summary>
///     The <c>event_type</c> catalog. It is a contract, not a convenience.
/// </summary>
/// <remarks>
///     The events tab and the per-node attempt list read these, and the per-attempt history the single-row node-run
///     schema does not keep lives in <see cref="NodeRetryScheduled" /> and <see cref="WorkSessionAttached" />. The
///     runtime extends this list by amendment, never silently.
/// </remarks>
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
