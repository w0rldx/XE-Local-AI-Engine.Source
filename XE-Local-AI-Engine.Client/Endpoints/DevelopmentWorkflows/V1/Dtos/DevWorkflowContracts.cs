namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using System.Text.Json;

// Requests. Route parameters and query parameters bind by name, so the property names here are the wire names.

public sealed class DevWorkflowWorkItemRequest
{
    public Guid WorkItemId { get; init; }
}

/// <summary>The list filter. <c>Status</c> is a <c>DevWorkflowWorkItemStatus</c> name; omitted means every status.</summary>
public sealed class ListDevWorkflowWorkItemsRequest
{
    public string? Status { get; init; }
}

public sealed class CreateDevWorkflowWorkItemRequest
{
    public string Title { get; init; } = string.Empty;

    /// <summary>The primary text: what the operator is asking for. Carried into the first agent's objective at run start.</summary>
    public string Request { get; init; } = string.Empty;

    /// <summary>
    ///     Optional, because a research-only workflow binds no repository at all. The real gate is at run start, which
    ///     refuses a graph containing repo-bound nodes when the work item names no project.
    /// </summary>
    public Guid? DevelopmentProjectId { get; init; }
}

/// <summary>A PATCH body: a null member leaves the stored value alone, it does not clear it.</summary>
public sealed class UpdateDevWorkflowWorkItemRequest
{
    public Guid WorkItemId { get; init; }

    public string? Title { get; init; }

    public string? Request { get; init; }
}

public sealed class ListDevWorkflowDefinitionsRequest
{
    /// <summary>Archived definitions are hidden by default: DELETE archives, so the picker would otherwise keep them.</summary>
    public bool IncludeArchived { get; init; }
}

public sealed class DevWorkflowDefinitionRequest
{
    public Guid DefinitionId { get; init; }
}

public sealed class CreateDevWorkflowDefinitionRequest
{
    public string Name { get; init; } = string.Empty;

    public DevWorkflowGraph Graph { get; init; } = DevWorkflowGraph.Empty;
}

/// <summary>
///     A PUT body carrying the version it was edited from. A stale one answers 409 rather than overwriting the edit
///     that landed in between.
/// </summary>
public sealed class UpdateDevWorkflowDefinitionRequest
{
    public Guid DefinitionId { get; init; }

    public int Version { get; init; }

    public string? Name { get; init; }

    public DevWorkflowGraph? Graph { get; init; }
}

public sealed class DevWorkflowRuleSetRequest
{
    public Guid RuleSetId { get; init; }
}

public sealed class CreateDevWorkflowRuleSetRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>The markdown injected verbatim into a matching node's context.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>Omitted means both axes empty, which applies the rule set to every node on this box.</summary>
    public DevWorkflowRuleScope? Scope { get; init; }

    public bool Enabled { get; init; } = true;
}

/// <summary>
///     A PUT body carrying the version it was edited from, and the WHOLE document: a rule set is edited as one, so an
///     omitted description clears it rather than meaning "leave whatever is there".
/// </summary>
public sealed class UpdateDevWorkflowRuleSetRequest
{
    public Guid RuleSetId { get; init; }

    public int Version { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Body { get; init; } = string.Empty;

    public DevWorkflowRuleScope? Scope { get; init; }

    public bool Enabled { get; init; } = true;
}

public sealed class ListDevWorkflowRunsRequest
{
    public Guid? WorkItemId { get; init; }

    /// <summary>A <c>DevWorkflowRunStatus</c> name. <c>WaitingForApproval</c> covers both an open gate and a blocked node.</summary>
    public string? Status { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed class DevWorkflowRunRequest
{
    public Guid RunId { get; init; }
}

/// <summary>
///     A run start. The work item comes from the route; the definition is a body field because it is a per-run choice.
/// </summary>
public sealed class StartDevWorkflowRunRequest
{
    public Guid WorkItemId { get; init; }

    /// <summary>The idempotency key. A replay of one answers with the run it already started, never a second run.</summary>
    public Guid OperationId { get; init; }

    public Guid DefinitionId { get; init; }

    /// <summary>Seeds every entry node run's input document alongside the work item's request. There is no run-level column for it.</summary>
    public string? InputsJson { get; init; }
}

public sealed class DevWorkflowRunActionRequest
{
    public Guid RunId { get; init; }

    public Guid OperationId { get; init; }
}

/// <summary>
///     The event feed. <c>SinceSeq</c> is an EXCLUSIVE lower bound, so a client that stores the sequence it last
///     rendered replays nothing it already has; 0 asks for everything.
/// </summary>
public sealed class DevWorkflowRunEventFeedRequest
{
    public Guid RunId { get; init; }

    public long SinceSeq { get; init; }

    public int Limit { get; init; } = 200;
}

public sealed class DevWorkflowNodeRunRequest
{
    public Guid RunId { get; init; }

    public Guid NodeRunId { get; init; }
}

public sealed class DevWorkflowDecisionRequest
{
    public Guid RunId { get; init; }

    public Guid NodeRunId { get; init; }

    /// <summary>Generated once when the gate opens and held until the request resolves, so a double-click replays rather than decides twice.</summary>
    public Guid OperationId { get; init; }

    /// <summary>A <c>DevWorkflowDecisionKind</c> name — the three gate answers and the three interventions share this route.</summary>
    public string Decision { get; init; } = string.Empty;

    public string? Comment { get; init; }

    /// <summary>The structured payload the gate declares — an edited plan, say. Operator prose, encrypted at rest.</summary>
    public string? PayloadJson { get; init; }
}

public sealed class DevWorkflowArtifactFeedRequest
{
    public Guid RunId { get; init; }

    public long SinceSeq { get; init; }
}

public sealed class DevWorkflowArtifactRequest
{
    public Guid RunId { get; init; }

    public Guid ArtifactId { get; init; }
}

// The wire graph. A field-for-field mirror of the stored graph document rather than a projection of it, so the mapper
// is a deserialize and nothing else — and so a definition read back, edited and saved keeps every field it arrived
// with. There is no edge table anywhere: this shape is composed from the encrypted graph blob on the definition row or
// the run row, which is the single source of routing truth.

public sealed record DevWorkflowGraph(int SchemaVersion,
    IReadOnlyList<DevWorkflowGraphNode> Nodes,
    IReadOnlyList<DevWorkflowGraphEdge> Edges,
    /// <summary>
    ///     The template's own waiver of the rule that a node writing outside its sandbox is reached through a human
    ///     gate. Absent means <c>false</c>: the rule is new, so nothing already stored can be relying on the waiver, and
    ///     a definition written before this field keeps every byte it had.
    /// </summary>
    bool? AllowUngatedWrites = null)
{
    public static DevWorkflowGraph Empty { get; } = new(1, [], []);
}

/// <summary>
///     <c>ToolMode</c> is what a Tool node does with the repository it names — <c>Validate</c> or <c>Apply</c> — and it
///     rides the wire because a definition that loses it loses its apply node: copying the seeded template through this
///     contract silently produced an ordinary validation node where "apply the approved patches" had been. Absent means
///     <c>Validate</c>, exactly as the runtime's parser reads it, so a definition written before this field keeps every
///     byte it had.
/// </summary>
public sealed record DevWorkflowGraphNode(
    string NodeKey,
    string NodeType,
    string Label,
    Guid? AgentDefinitionId,
    string? AgentSeedSlug,
    string? Instructions,
    string? ModelProfile,
    string? ReasoningEffort,
    IReadOnlyList<string>? ValidationCommandIds,
    string? JoinPolicy,
    int? MaxAttempts,
    int? RetryDelaySeconds,
    int? NodeTimeoutSeconds,
    string? RetryTarget,
    DevWorkflowMaterialization? Materialization,
    IReadOnlyDictionary<string, string>? RequiredCapabilities,
    string? ToolMode,
    /// <summary>
    ///     How many times this node's fix loop may re-run before the run stops and asks a human. Only meaningful beside
    ///     a <c>RetryTarget</c>, and refused without one. Absent means no per-loop cap at all — the run-wide attempt
    ///     budget is what bounds it then, exactly as it does today.
    /// </summary>
    int? MaxLoopIterations,
    /// <summary>
    ///     Whether this node belongs to a materialization template subtree — a clone-in-waiting the run gives no node
    ///     run to. DERIVED on the way out from the runtime's own parser, never authored and never stored: the save path
    ///     nulls it, so a graph that round-trips through a definition PUT keeps exactly the bytes it arrived with.
    /// </summary>
    bool? IsTemplate = null);

public sealed record DevWorkflowMaterialization(string TemplateNodeKey, string ArtifactKind, string JoinNodeKey, int MaxChildren);

/// <summary>Conditions live on edges only: a gate's decision IS which of its out-edges matched.</summary>
public sealed record DevWorkflowGraphEdge(string From, string To, DevWorkflowEdgeCondition? Condition);

/// <summary>
///     <see cref="Value" /> is a JSON scalar — string, number, boolean or null — and not a string member. A boolean
///     that round-trips as <c>"true"</c> would compare against a real boolean as a type mismatch, and the evaluator
///     fails closed, so the edge would silently never fire.
///     <para>
///         Nullable so that the two operators which take no value (<c>exists</c>, <c>notExists</c>) round-trip as the
///         absent member they are stored as, rather than as an unwritable undefined element.
///     </para>
/// </summary>
public sealed record DevWorkflowEdgeCondition(string Path, string Op, JsonElement? Value);

// Responses. Enums cross the wire as their NAMES and are typed string here; the client re-narrows them.

public sealed record DevWorkflowWorkItemResponse(
    Guid Id,
    string Title,
    string Request,
    Guid? DevelopmentProjectId,
    string Status,
    Guid? LatestRunId,
    IReadOnlyList<DevWorkflowRunSummaryResponse> Runs,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long Version);

/// <summary>
///     One row of the work-item list, wide enough to render the page without a per-row fetch — which is what makes the
///     list's poll honest rather than a fan-out.
/// </summary>
public sealed record DevWorkflowWorkItemSummaryResponse(
    Guid Id,
    string Title,
    Guid? DevelopmentProjectId,
    string Status,
    Guid? LatestRunId,
    string? LatestRunStatus,
    string? DefinitionName,
    int QueuedNodeCount,
    int RunningNodeCount,
    int CompletedNodeCount,
    int TotalNodeCount,
    long UpdatedAtUtc);

public sealed record DevWorkflowDefinitionResponse(
    Guid Id,
    string Name,
    DevWorkflowGraph Graph,
    string GraphHash,
    string Source,
    string? SeedSlug,
    bool Archived,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record DevWorkflowDefinitionSummaryResponse(
    Guid Id,
    string Name,
    string Source,
    string? SeedSlug,
    bool Archived,
    int Version,
    int NodeCount,
    long UpdatedAtUtc);

/// <summary>
///     One run in full. The pinned <see cref="Graph" /> and every node summary ride together because the graph view
///     paints them together: this is the one fetch a change notification triggers.
/// </summary>
public sealed record DevWorkflowRunResponse(
    Guid Id,
    Guid WorkItemId,
    Guid DefinitionId,
    int DefinitionVersion,
    string? DefinitionName,
    int GraphRevision,
    DevWorkflowGraph Graph,
    string Status,
    IReadOnlyList<DevWorkflowNodeRunSummaryResponse> Nodes,
    int QueuedNodeCount,
    int RunningNodeCount,
    int PendingDecisionCount,
    Guid? BlockingGateNodeRunId,
    string? FailureClass,
    string? TerminalReason,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long Version,
    long LastSequence,

    /// <summary>
    ///     The run's cost, summed over the node runs already on this response. A LOWER bound by construction: it is the
    ///     final attempt of each node, so a run that retried spent more. The runbook's total is this plus the run's
    ///     <c>node.retry.scheduled</c> details.
    /// </summary>
    DevWorkflowRunCostResponse Cost);

/// <summary>
///     Where one terminal node run routed, parsed off the node run's own <c>route_json</c>.
/// </summary>
/// <param name="Satisfied">
///     The out-edges whose condition fired. This means "the edge was satisfied", NEVER "the successor ran": admission
///     is a question about a target's INBOUND edges, so an <c>All</c> join can still skip on a dead sibling edge and an
///     <c>Any</c> join can admit on one. For a human gate, the node's own output document is authoritative.
/// </param>
/// <param name="Dead">The out-edges whose condition did not fire.</param>
/// <param name="Waived">
///     The out-edges of a node run whose SKIP the state machine waived — an operator's own skip rather than one that
///     cascaded off something dead. Its own bucket because neither of the others is true of it: a waived edge does not
///     admit an <c>Any</c> successor the way a satisfied one does, and it does not kill an <c>All</c> one the way a
///     dead one does. Empty on a row written before this bucket existed, which is also what it means.
/// </param>
/// <param name="GateAnswer">The decision a human gate settled on; null on every other node type.</param>
/// <param name="Truncated">
///     Whether keys were dropped to keep the stored document inside its column bound. A truncated route must be shown
///     as truncated, or a short list reads as the whole one.
/// </param>
public sealed record DevWorkflowNodeRouteResponse(IReadOnlyList<string> Satisfied,
    IReadOnlyList<string> Dead,
    IReadOnlyList<string> Waived,
    string? GateAnswer,
    bool Truncated);

/// <summary>
///     A run's headline spend, summed over its node runs' final attempts. Every member is null until some node run
///     reports one, because "nobody measured" and "zero" are different answers.
/// </summary>
public sealed record DevWorkflowRunCostResponse(long? InputTokens,
    long? OutputTokens,
    int? ToolCalls,
    int? ProviderCalls,
    long? AgentTurnMs);

public sealed record DevWorkflowRunSummaryResponse(
    Guid Id,
    Guid WorkItemId,
    Guid DefinitionId,
    string? DefinitionName,
    string Status,
    int QueuedNodeCount,
    int RunningNodeCount,
    int CompletedNodeCount,
    int TotalNodeCount,
    int PendingDecisionCount,
    Guid? BlockingGateNodeRunId,
    long? StartedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     Everything a node card paints, resolved in the run-detail query. No card needs a follow-up request, which is
///     the whole reason there is no node-run list route.
/// </summary>
public sealed record DevWorkflowNodeRunSummaryResponse(
    Guid Id,
    string NodeKey,
    string NodeType,
    string Label,
    string Status,
    int Attempt,
    int MaxAttempts,
    string? QueueReason,
    long? QueuedAtUtc,
    IReadOnlyList<string>? WaitingOnNodeKeys,
    string? PendingDecisionKind,
    bool IsMaterialized,
    string? MaterializedFromNodeKey,
    int? MaterializationIndex,
    /// <summary>
    ///     The node run this clone was materialized FROM, which is the group identifier: one decompose node run
    ///     materializes once, so its id names that fan-out for the life of the run.
    /// </summary>
    Guid? MaterializationGroupId,
    /// <summary>
    ///     How many siblings the group holds, counted server-side over the run's WHOLE node-run list. Counted here
    ///     rather than in the browser because a client can only count the rows it has drawn, which is wrong by
    ///     construction for a fan-out wider than the page it rendered.
    /// </summary>
    int? MaterializationCount,
    Guid? DevelopmentProjectId,
    Guid? DevelopmentTaskId,
    Guid? AgentDefinitionId,
    string? AgentDisplayName,
    string? ModelLabel,
    bool HasStaleInputs,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long Sequence,
    /// <summary>
    ///     For a <c>Skipped</c> row only: whether the state machine WAIVES this skip, so a downstream <c>All</c> join
    ///     carries on past it as long as a sibling arrived. <c>false</c> means the skip is dead and the join will skip
    ///     with it; <c>null</c> means the question does not apply — any other status — or that the pinned graph could
    ///     not be routed to answer it.
    ///     <para>
    ///         Computed on the SERVER because it cannot be read off this row. A skip an operator chose and one that
    ///         cascaded off a Failed ancestor are the same status, and the ancestor that decides which is which is not
    ///         necessarily among the join's own dependencies — so a client judging by status alone tells an operator
    ///         the join carries on in exactly the case where the runtime skips it. The verdict comes from the same
    ///         predicate the dispatcher admits by (<c>DevWorkflowStateMachine.WaivedSkipNodeKeys</c>), which is what
    ///         stops the two from drifting.
    ///     </para>
    /// </summary>
    bool? SkipWaived,
    /// <summary>
    ///     What the node run's LAST attempt cost, three headline numbers of the twelve the drill-down carries. Null on
    ///     a row with nothing to report — a structural node, a row written before this was collected, or a collection
    ///     that could not run — which is not the same as zero. Earlier attempts live on the run's
    ///     <c>node.retry.scheduled</c> events, never here.
    /// </summary>
    long? InputTokens,
    long? OutputTokens,
    int? ToolCalls,
    /// <summary>
    ///     The row is a <c>Succeeded</c> check that had nothing to check — the verdict a zero-task decomposition seeds
    ///     onto its template's validations (D12). Carried on the SUMMARY rather than left to the drill-down because the
    ///     run header counts these rows and the node table renders them: without it a run that decomposed into no work
    ///     reports its template check as completed work, which is the one thing that row does not stand for.
    /// </summary>
    bool ValidationNotApplicable);

/// <summary>
///     The drill-down. <see cref="WorkSessionId" /> is the whole of the agent view: it links out to the EXISTING
///     work-session routes rather than this surface growing observability endpoints of its own.
/// </summary>
public sealed record DevWorkflowNodeRunDetailResponse(
    Guid Id,
    Guid RunId,
    string NodeKey,
    string NodeType,
    string Label,
    string Status,
    int Attempt,
    int MaxAttempts,
    int SessionResumes,
    string? QueueReason,
    long? QueuedAtUtc,
    Guid? AgentDefinitionId,
    string? AgentDisplayName,
    string? ModelLabel,
    Guid? WorkSessionId,
    Guid? ConversationId,
    bool WorkSessionAvailable,
    Guid? DevelopmentProjectId,
    Guid? DevelopmentTaskId,
    Guid? PrimaryArtifactId,
    string? Instructions,
    string? InputJson,
    string? OutputJson,
    IReadOnlyList<Guid> ProducedArtifactIds,
    IReadOnlyList<Guid> ConsumedArtifactIds,
    IReadOnlyList<DevWorkflowAppliedRuleSetResponse> AppliedRuleSets,
    string? PendingDecisionKind,
    IReadOnlyList<string> AllowedDecisions,
    bool HasRejectBranch,
    string? FailureClass,
    string? TerminalReason,
    IReadOnlyList<DevWorkflowDecisionResponse> Decisions,
    long? StartedAtUtc,
    long? CompletedAtUtc,
    long Sequence,

    /// <summary>
    ///     What this node run's LAST attempt spent on the provider. Null means nobody reported it, never zero: the
    ///     columns are cleared by the <c>Pending</c> reset a re-attempt writes, so earlier attempts are on the run's
    ///     <c>node.retry.scheduled</c> events and a total is <c>this + those</c>.
    /// </summary>
    long? InputTokens,
    long? OutputTokens,
    long? ReasoningTokens,

    /// <summary>A character-profile estimate the agent loop made. Quote it only where <see cref="InputTokens" /> is null.</summary>
    long? EstimatedInputTokens,
    int? ProviderCalls,
    int? ToolCalls,

    /// <summary>Schema tokens SHIPPED across rounds, which is a cost, not the size of the schema.</summary>
    long? ToolSchemaTokens,

    /// <summary>
    ///     The distinct tools this node run's session called, names only and capped. A last element of <c>"…"</c> is a
    ///     truncation marker rather than a tool. Null means there were no work-session step rows to read — a DevTask,
    ///     Tool, Gate, Parallel or Join row — and never "this node called no tools", which is what
    ///     <see cref="ToolCalls" /> answers.
    /// </summary>
    IReadOnlyList<string>? ToolNames,

    /// <summary>
    ///     Wall-clock time inside the agent's chat turns, tool loop included — the envelope measures a whole run, and
    ///     no provider-round-only duration is persisted anywhere this collector can read. So the node's runtime minus
    ///     this is time spent OUTSIDE the turns, which is not the same thing as tool time and must never be labelled
    ///     as it.
    /// </summary>
    long? AgentTurnMs,

    /// <summary>
    ///     The model that actually served the last turn — the receipt, as opposed to <see cref="ModelLabel" />, which
    ///     is what the node or its agent ASKED for. Both are present because they can differ.
    /// </summary>
    string? ServedModelName,

    /// <summary>Where a terminal node run routed. Null while it has not finished, because it has routed nowhere yet.</summary>
    DevWorkflowNodeRouteResponse? Route,

    /// <summary>How many steps the node run's work session took. Zero is a measurement; null is an absence.</summary>
    int? WorkSessionSteps,

    /// <summary>
    ///     <see cref="FailureClass" /> projected onto the ONE cross-unit vocabulary
    ///     (<c>AgentUnitFailureClass</c>), so a workflow node run, a chat run envelope and a Development attempt can be
    ///     grouped together in a report. Null exactly when the row records no failure. Nothing routes on it.
    /// </summary>
    string? FailureClassGroup);

/// <summary>
///     Which rule text actually applied, by content hash. Names the document without copying its body, so the audit
///     stays truthful — and verifiable — after the rule set is edited or deleted.
///     <para>
///         <see cref="ContentSha256" /> is what the node run RECORDED and never changes.
///         <see cref="CurrentContentSha256" /> is what the rule set holds now, or null when it has since been deleted —
///         so a reader can say "edited since this ran" or "deleted" instead of having to assume the document still says
///         what it said. Comparing the two is the whole reason the hash is recorded.
///     </para>
/// </summary>
public sealed record DevWorkflowAppliedRuleSetResponse(Guid Id, string Name, string ContentSha256, string? CurrentContentSha256);

/// <summary>
///     Where a rule set applies. An EMPTY axis means "matches everything"; a populated one is an exact,
///     case-insensitive membership test — no globbing, no precedence, no expression language. Everything applicable is
///     injected.
///     <para>
///         Two axes, not four. <c>languages</c> and <c>taskTypes</c> were dropped before they shipped because nothing
///         produces either value: under "every populated axis must match" they could only ever apply to nothing, while
///         looking on the wire as though they worked.
///     </para>
/// </summary>
public sealed record DevWorkflowRuleScope(IReadOnlyList<Guid> ProjectIds, IReadOnlyList<string> NodeTypes);

public sealed record DevWorkflowRuleSetResponse(
    Guid Id,
    string Name,
    string? Description,
    string Body,
    DevWorkflowRuleScope Scope,
    bool Enabled,
    string ContentSha256,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     A rule set WITHOUT its body — the list draws names, scopes and hashes. <see cref="ContentSha256" /> is here
///     because it is the half a reader compares against a node run's recorded hash to see whether the document has
///     moved on since it applied.
/// </summary>
public sealed record DevWorkflowRuleSetSummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    DevWorkflowRuleScope Scope,
    bool Enabled,
    string ContentSha256,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

public sealed record DevWorkflowRunEventResponse(
    Guid Id,
    long Sequence,
    string EventType,
    Guid? NodeRunId,
    string? Outcome,
    string? DetailJson,
    Guid? OperationId,
    long OccurredAtUtc);

/// <summary>
///     An artifact's metadata. There is deliberately no member for the blob reference the node stores it under: it is
///     a host path, it is of no use to a client, and a response is the one place it could leak from.
/// </summary>
public sealed record DevWorkflowArtifactResponse(
    Guid Id,
    Guid LineageId,
    int Version,
    long Sequence,
    string Kind,
    string Name,
    string MediaType,
    string ContentSha256,
    long SizeBytes,
    Guid ProducedByNodeRunId,
    string ProducingNodeKey,
    bool IsValid,
    bool IsStale,
    Guid? StaleBecauseArtifactId,
    string? StaleReason,
    bool IsLatest,
    long CreatedAtUtc);

/// <summary>
///     An artifact with its bytes. <see cref="IsBase64" /> is decided from the media type, never by sniffing the
///     bytes, so binary content is never handed over as mangled UTF-8.
/// </summary>
public sealed record DevWorkflowArtifactContentResponse(DevWorkflowArtifactResponse Artifact, string Content, bool IsBase64);

public sealed record DevWorkflowDecisionResponse(
    Guid Id,
    Guid NodeRunId,
    int Attempt,
    string Decision,
    string? Comment,
    string? DecidedBySubject,
    long DecidedAtUtc,
    Guid OperationId,
    long Sequence);

/// <summary>What the decision endpoint answers: the recorded act, plus where the run and node run now stand.</summary>
public sealed record DevWorkflowDecisionResultResponse(DevWorkflowDecisionResponse Decision, string RunStatus, string NodeRunStatus);

// One concrete response record per list rather than one generic envelope: NSwag builds schema ids from the CLR type
// name, and a generic would land in the generated client as an unreadable ListDevWorkflowFeedResponseOfT.

public sealed record ListDevWorkflowWorkItemsResponse(IReadOnlyList<DevWorkflowWorkItemSummaryResponse> Items);

public sealed record ListDevWorkflowDefinitionsResponse(IReadOnlyList<DevWorkflowDefinitionSummaryResponse> Items);

public sealed record ListDevWorkflowRuleSetsResponse(IReadOnlyList<DevWorkflowRuleSetSummaryResponse> Items);

public sealed record ListDevWorkflowRunsResponse(IReadOnlyList<DevWorkflowRunSummaryResponse> Items);

/// <summary>
///     A page of events. <see cref="HasMore" /> is reported from a one-over-the-limit probe rather than inferred, and
///     the client follows it by re-reading from <see cref="LastSequence" />.
/// </summary>
public sealed record ListDevWorkflowRunEventsResponse(IReadOnlyList<DevWorkflowRunEventResponse> Items, long LastSequence, bool HasMore);

public sealed record ListDevWorkflowArtifactsResponse(IReadOnlyList<DevWorkflowArtifactResponse> Items, long LastSequence);
