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

    /// <summary>
    ///     <c>required</c> is what puts <c>version</c> in the schema's <c>required</c> array, and it is the only thing
    ///     that does.
    /// </summary>
    /// <remarks>
    ///     The validator's <c>GreaterThan(0)</c> is the one rule shape FastEndpoints' schema processor does not read
    ///     requiredness from, so without <c>required</c> a generated caller could omit the member and get a 400 from a
    ///     contract that never said the field was mandatory.
    /// </remarks>
    public required int Version { get; init; }

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

    /// <summary>See <see cref="UpdateDevWorkflowDefinitionRequest.Version" /> for why this carries <c>required</c>.</summary>
    public required int Version { get; init; }

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

// The wire graph: a field-for-field mirror of the stored graph document rather than a projection, so the mapper is a deserialize and nothing else, and a definition read
// back, edited and saved keeps every field it arrived with. There is no edge table — the shape is composed from the encrypted graph blob, the single source of routing truth.

public sealed record DevWorkflowGraph(
    int SchemaVersion,
    IReadOnlyList<DevWorkflowGraphNode> Nodes,
    IReadOnlyList<DevWorkflowGraphEdge> Edges,
    /// <summary>
    ///     The template's own waiver of the rule that a node writing outside its sandbox is reached through a human
    ///     gate. Absent means <c>false</c> — nothing stored can rely on the waiver — so a definition written without
    ///     the field keeps every byte it had.
    /// </summary>
    bool? AllowUngatedWrites = null)
{
    public static DevWorkflowGraph Empty { get; } = new(1, [], []);
}

/// <summary>
///     <c>ToolMode</c> is what a Tool node does with the repository it names — <c>Validate</c> or <c>Apply</c>.
/// </summary>
/// <remarks>
///     It rides the wire because a definition that loses it loses its apply node: copying the seeded template through
///     a contract without it turns an "apply the approved patches" node into an ordinary validation one. Absent means
///     <c>Validate</c>, exactly as the runtime's parser reads it, so a definition written WITHOUT this field keeps
///     every byte it had.
/// </remarks>
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
    ///     a <c>RetryTarget</c>, and refused without one. Absent means NO per-loop cap: the run-wide attempt budget is
    ///     what bounds it then.
    /// </summary>
    int? MaxLoopIterations,
    /// <summary>
    ///     Whether this node belongs to a materialization template subtree — a clone-in-waiting with no node run.
    ///     DERIVED from the parser on the way out, never authored or stored, so a graph round-tripping a PUT keeps the
    ///     bytes it arrived with.
    /// </summary>
    bool? IsTemplate = null);

public sealed record DevWorkflowMaterialization(string TemplateNodeKey, string ArtifactKind, string JoinNodeKey, int MaxChildren);

/// <summary>Conditions live on edges only: a gate's decision IS which of its out-edges matched.</summary>
public sealed record DevWorkflowGraphEdge(string From, string To, DevWorkflowEdgeCondition? Condition);

/// <summary>
///     <see cref="Value" /> is a JSON scalar — string, number, boolean or null — and not a string member.
/// </summary>
/// <remarks>
///     A boolean that round-trips as <c>"true"</c> would compare against a real boolean as a type mismatch, and the
///     evaluator fails closed, so the edge would silently never fire. Nullable so that the two operators which take no
///     value (<c>exists</c>, <c>notExists</c>) round-trip as the absent member they are stored as, rather than as an
///     unwritable undefined element.
/// </remarks>
public sealed record DevWorkflowEdgeCondition(string Path, string Op, JsonElement? Value);

// Responses. Enums cross the wire as their NAMES and are typed string here; the client re-narrows them.

public sealed class DevWorkflowWorkItemResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Request { get; init; }

    public required Guid? DevelopmentProjectId { get; init; }

    public required string Status { get; init; }

    public required Guid? LatestRunId { get; init; }

    public required IReadOnlyList<DevWorkflowRunSummaryResponse> Runs { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }
}

/// <summary>
///     One row of the work-item list, wide enough to render the page without a per-row fetch — which is what makes the
///     list's poll honest rather than a fan-out.
/// </summary>
public sealed class DevWorkflowWorkItemSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required Guid? DevelopmentProjectId { get; init; }

    public required string Status { get; init; }

    public required Guid? LatestRunId { get; init; }

    public required string? LatestRunStatus { get; init; }

    public required string? DefinitionName { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    public required int CompletedNodeCount { get; init; }

    public required int TotalNodeCount { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class DevWorkflowDefinitionResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required DevWorkflowGraph Graph { get; init; }

    public required string GraphHash { get; init; }

    public required string Source { get; init; }

    public required string? SeedSlug { get; init; }

    public required bool Archived { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class DevWorkflowDefinitionSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Source { get; init; }

    public required string? SeedSlug { get; init; }

    public required bool Archived { get; init; }

    public required int Version { get; init; }

    public required int NodeCount { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     One run in full. The pinned <see cref="Graph" /> and every node summary ride together because the graph view
///     paints them together: this is the one fetch a change notification triggers.
/// </summary>
public sealed class DevWorkflowRunResponse
{
    public required Guid Id { get; init; }

    public required Guid WorkItemId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required string? DefinitionName { get; init; }

    public required int GraphRevision { get; init; }

    public required DevWorkflowGraph Graph { get; init; }

    public required string Status { get; init; }

    public required IReadOnlyList<DevWorkflowNodeRunSummaryResponse> Nodes { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    public required int PendingDecisionCount { get; init; }

    public required Guid? BlockingGateNodeRunId { get; init; }

    public required string? FailureClass { get; init; }

    public required string? TerminalReason { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long Version { get; init; }

    public required long LastSequence { get; init; }

    public required DevWorkflowRunCostResponse Cost { get; init; }
}

/// <summary>
///     Where one terminal node run routed, parsed off the node run's own <c>route_json</c>.
/// </summary>
public sealed class DevWorkflowNodeRouteResponse
{
    /// <summary>
    ///     The out-edges whose condition fired. This means "the edge was satisfied", NEVER "the successor ran".
    /// </summary>
    /// <remarks>
    ///     Admission is a question about a target's INBOUND edges, so an <c>All</c> join can still skip on a dead
    ///     sibling edge and an <c>Any</c> join can admit on one. For a human gate, the node's own output document is
    ///     authoritative.
    /// </remarks>
    public required IReadOnlyList<string> Satisfied { get; init; }

    /// <summary>The out-edges whose condition did not fire.</summary>
    public required IReadOnlyList<string> Dead { get; init; }

    /// <summary>
    ///     The out-edges of a node run whose SKIP the state machine waived — an operator's own skip rather than one
    ///     that cascaded off something dead.
    /// </summary>
    /// <remarks>
    ///     Its own bucket because neither of the others is true of it: a waived edge does not admit an <c>Any</c>
    ///     successor the way a satisfied one does, and it does not kill an <c>All</c> one the way a dead one does.
    ///     Empty on a row written WITHOUT this bucket, which is also what empty means.
    /// </remarks>
    public required IReadOnlyList<string> Waived { get; init; }

    /// <summary>The decision a human gate settled on; null on every other node type.</summary>
    public required string? GateAnswer { get; init; }

    /// <summary>
    ///     Whether keys were dropped to keep the stored document inside its column bound. A truncated route must be shown
    ///     as truncated, or a short list reads as the whole one.
    /// </summary>
    public required bool Truncated { get; init; }
}

/// <summary>
///     A run's headline spend, summed over its node runs' final attempts. Every member is null until some node run
///     reports one, because "nobody measured" and "zero" are different answers.
/// </summary>
public sealed class DevWorkflowRunCostResponse
{
    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }

    public required int? ToolCalls { get; init; }

    public required int? ProviderCalls { get; init; }

    public required long? AgentTurnMs { get; init; }
}

public sealed class DevWorkflowRunSummaryResponse
{
    public required Guid Id { get; init; }

    public required Guid WorkItemId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required string? DefinitionName { get; init; }

    public required string Status { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    public required int CompletedNodeCount { get; init; }

    public required int TotalNodeCount { get; init; }

    public required int PendingDecisionCount { get; init; }

    public required Guid? BlockingGateNodeRunId { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     Everything a node card paints, resolved in the run-detail query. No card needs a follow-up request, which is
///     the whole reason there is no node-run list route.
/// </summary>
public sealed class DevWorkflowNodeRunSummaryResponse
{
    public required Guid Id { get; init; }

    public required string NodeKey { get; init; }

    public required string NodeType { get; init; }

    public required string Label { get; init; }

    public required string Status { get; init; }

    public required int Attempt { get; init; }

    public required int MaxAttempts { get; init; }

    public required string? QueueReason { get; init; }

    public required long? QueuedAtUtc { get; init; }

    public required IReadOnlyList<string>? WaitingOnNodeKeys { get; init; }

    public required string? PendingDecisionKind { get; init; }

    public required bool IsMaterialized { get; init; }

    public required string? MaterializedFromNodeKey { get; init; }

    public required int? MaterializationIndex { get; init; }

    public required Guid? MaterializationGroupId { get; init; }

    public required int? MaterializationCount { get; init; }

    public required Guid? DevelopmentProjectId { get; init; }

    public required Guid? DevelopmentTaskId { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required string? AgentDisplayName { get; init; }

    public required string? ModelLabel { get; init; }

    public required bool HasStaleInputs { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long Sequence { get; init; }

    public required int OperatorRetries { get; init; }

    public required bool? SkipWaived { get; init; }

    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }

    public required int? ToolCalls { get; init; }

    public required bool ValidationNotApplicable { get; init; }
}

/// <summary>
///     The drill-down. <see cref="WorkSessionId" /> is the whole of the agent view: it links out to the EXISTING
///     work-session routes rather than this surface growing observability endpoints of its own.
/// </summary>
public sealed class DevWorkflowNodeRunDetailResponse
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required string NodeKey { get; init; }

    public required string NodeType { get; init; }

    public required string Label { get; init; }

    public required string Status { get; init; }

    public required int Attempt { get; init; }

    public required int MaxAttempts { get; init; }

    public required int SessionResumes { get; init; }

    public required string? QueueReason { get; init; }

    public required long? QueuedAtUtc { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required string? AgentDisplayName { get; init; }

    public required string? ModelLabel { get; init; }

    public required Guid? WorkSessionId { get; init; }

    public required Guid? ConversationId { get; init; }

    public required bool WorkSessionAvailable { get; init; }

    public required Guid? DevelopmentProjectId { get; init; }

    public required Guid? DevelopmentTaskId { get; init; }

    public required Guid? PrimaryArtifactId { get; init; }

    public required string? Instructions { get; init; }

    public required string? InputJson { get; init; }

    public required string? OutputJson { get; init; }

    public required IReadOnlyList<Guid> ProducedArtifactIds { get; init; }

    public required IReadOnlyList<Guid> ConsumedArtifactIds { get; init; }

    public required IReadOnlyList<DevWorkflowAppliedRuleSetResponse> AppliedRuleSets { get; init; }

    public required string? PendingDecisionKind { get; init; }

    public required IReadOnlyList<string> AllowedDecisions { get; init; }

    public required bool HasRejectBranch { get; init; }

    public required string? FailureClass { get; init; }

    public required string? TerminalReason { get; init; }

    public required IReadOnlyList<DevWorkflowDecisionResponse> Decisions { get; init; }

    public required int OperatorRetries { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long Sequence { get; init; }

    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }

    public required long? ReasoningTokens { get; init; }

    public required long? EstimatedInputTokens { get; init; }

    public required int? ProviderCalls { get; init; }

    public required int? ToolCalls { get; init; }

    public required long? ToolSchemaTokens { get; init; }

    public required IReadOnlyList<string>? ToolNames { get; init; }

    public required long? AgentTurnMs { get; init; }

    public required string? ServedModelName { get; init; }

    public required DevWorkflowNodeRouteResponse? Route { get; init; }

    public required int? WorkSessionSteps { get; init; }

    public required string? FailureClassGroup { get; init; }

    public required long? ModelReadinessMs { get; init; }

    public required long? VramFreeAtLoadBytes { get; init; }

    public required long? VramAdmittedBytes { get; init; }
}

/// <summary>
///     Which rule text actually applied, by content hash: it names the document without copying its body, so the audit
///     stays truthful — and verifiable — after the rule set is edited or deleted.
/// </summary>
/// <remarks>
///     <see cref="ContentSha256" /> is what the node run RECORDED and never changes.
///     <see cref="CurrentContentSha256" /> is what the rule set holds now, or null when it has since been deleted — so
///     a reader can say "edited since this ran" or "deleted" instead of having to assume the document still says what
///     it said. Comparing the two is the whole reason the hash is recorded.
/// </remarks>
public sealed class DevWorkflowAppliedRuleSetResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string ContentSha256 { get; init; }

    public required string? CurrentContentSha256 { get; init; }
}

/// <summary>
///     Where a rule set applies: an EMPTY axis means "matches everything", a populated one is an exact,
///     case-insensitive membership test — no globbing, no precedence, no expression language.
/// </summary>
/// <remarks>
///     Everything applicable is injected. Two axes, not four: <c>languages</c> and <c>taskTypes</c> are absent because
///     nothing produces either value, so under "every populated axis must match" they could only ever apply to
///     nothing, while looking on the wire as though they worked.
/// </remarks>
public sealed record DevWorkflowRuleScope(IReadOnlyList<Guid> ProjectIds, IReadOnlyList<string> NodeTypes);

public sealed class DevWorkflowRuleSetResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string Body { get; init; }

    public required DevWorkflowRuleScope Scope { get; init; }

    public required bool Enabled { get; init; }

    public required string ContentSha256 { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     A rule set WITHOUT its body — the list draws names, scopes and hashes. <see cref="ContentSha256" /> is here
///     because it is the half a reader compares against a node run's recorded hash to see whether the document has
///     moved on since it applied.
/// </summary>
public sealed class DevWorkflowRuleSetSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required DevWorkflowRuleScope Scope { get; init; }

    public required bool Enabled { get; init; }

    public required string ContentSha256 { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class DevWorkflowRunEventResponse
{
    public required Guid Id { get; init; }

    public required long Sequence { get; init; }

    public required string EventType { get; init; }

    public required Guid? NodeRunId { get; init; }

    public required string? Outcome { get; init; }

    public required string? DetailJson { get; init; }

    public required Guid? OperationId { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>
///     An artifact's metadata. There is deliberately no member for the blob reference the node stores it under: it is
///     a host path, it is of no use to a client, and a response is the one place it could leak from.
/// </summary>
public sealed class DevWorkflowArtifactResponse
{
    public required Guid Id { get; init; }

    public required Guid LineageId { get; init; }

    public required int Version { get; init; }

    public required long Sequence { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required string MediaType { get; init; }

    public required string ContentSha256 { get; init; }

    public required long SizeBytes { get; init; }

    public required Guid ProducedByNodeRunId { get; init; }

    public required string ProducingNodeKey { get; init; }

    public required bool IsValid { get; init; }

    public required bool IsStale { get; init; }

    public required Guid? StaleBecauseArtifactId { get; init; }

    public required string? StaleReason { get; init; }

    public required bool IsLatest { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     An artifact with its bytes. <see cref="IsBase64" /> is decided from the media type, never by sniffing the
///     bytes, so binary content is never handed over as mangled UTF-8.
/// </summary>
public sealed class DevWorkflowArtifactContentResponse
{
    public required DevWorkflowArtifactResponse Artifact { get; init; }

    public required string Content { get; init; }

    public required bool IsBase64 { get; init; }
}

public sealed class DevWorkflowDecisionResponse
{
    public required Guid Id { get; init; }

    public required Guid NodeRunId { get; init; }

    public required int Attempt { get; init; }

    public required string Decision { get; init; }

    public required string? Comment { get; init; }

    public required string? DecidedBySubject { get; init; }

    public required long DecidedAtUtc { get; init; }

    public required Guid OperationId { get; init; }

    public required long Sequence { get; init; }
}

/// <summary>What the decision endpoint answers: the recorded act, plus where the run and node run now stand.</summary>
public sealed class DevWorkflowDecisionResultResponse
{
    public required DevWorkflowDecisionResponse Decision { get; init; }

    public required string RunStatus { get; init; }

    public required string NodeRunStatus { get; init; }
}

// One concrete response type per list rather than one generic envelope: NSwag builds schema ids from the CLR type
// name, and a generic would land in the generated client as an unreadable ListDevWorkflowFeedResponseOfT.

public sealed class ListDevWorkflowWorkItemsResponse
{
    public required IReadOnlyList<DevWorkflowWorkItemSummaryResponse> Items { get; init; }
}

public sealed class ListDevWorkflowDefinitionsResponse
{
    public required IReadOnlyList<DevWorkflowDefinitionSummaryResponse> Items { get; init; }
}

public sealed class ListDevWorkflowRuleSetsResponse
{
    public required IReadOnlyList<DevWorkflowRuleSetSummaryResponse> Items { get; init; }
}

public sealed class ListDevWorkflowRunsResponse
{
    public required IReadOnlyList<DevWorkflowRunSummaryResponse> Items { get; init; }
}

/// <summary>
///     A page of events. <see cref="HasMore" /> is reported from a one-over-the-limit probe rather than inferred, and
///     the client follows it by re-reading from <see cref="LastSequence" />.
/// </summary>
public sealed class ListDevWorkflowRunEventsResponse
{
    public required IReadOnlyList<DevWorkflowRunEventResponse> Items { get; init; }

    public required long LastSequence { get; init; }

    public required bool HasMore { get; init; }
}

public sealed class ListDevWorkflowArtifactsResponse
{
    public required IReadOnlyList<DevWorkflowArtifactResponse> Items { get; init; }

    public required long LastSequence { get; init; }
}

/// <summary>
///     Whether this node serves development workflows at all — the one response every node answers, switch on or off.
/// </summary>
/// <remarks>
///     The rest of the family is 404ed by request-path middleware when <c>DevWorkflows:Enabled</c> is false, and a
///     bodyless 404 is indistinguishable from a broken route, so without this the SPA can only say "could not load".
///     Deliberately one field — Development's richer capability payload reports a sandbox this family does not have.
/// </remarks>
public sealed class DevWorkflowCapabilityResponse
{
    public required bool Enabled { get; init; }
}
