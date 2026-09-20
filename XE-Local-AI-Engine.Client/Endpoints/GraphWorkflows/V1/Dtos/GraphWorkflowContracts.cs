namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using System.Text.Json;
using System.Text.Json.Serialization;

// Requests. Route parameters bind by name, so the property names here are the wire names.

public sealed class GraphWorkflowDefinitionRequest
{
    public Guid DefinitionId { get; init; }
}

public sealed class CreateGraphWorkflowDefinitionRequest
{
    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public GraphWorkflowGraph Graph { get; init; } = GraphWorkflowGraph.Empty;
}

/// <summary>
///     A PUT body carrying the version it was edited from. A stale one answers 409 rather than overwriting the edit
///     that landed in between. Every optional member left null leaves the stored value alone.
/// </summary>
public sealed class UpdateGraphWorkflowDefinitionRequest
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

    /// <summary>
    ///     Null leaves the stored description alone; an EMPTY string clears it. The two are distinct on purpose — a
    ///     rename that carries no description must not wipe one, and an author who deleted the text must be able to
    ///     say so without a second verb.
    /// </summary>
    public string? Description { get; init; }

    public GraphWorkflowGraph? Graph { get; init; }
}

/// <summary>A graph to judge without saving. Persists nothing — the answer is a report, not a write.</summary>
public sealed class ValidateGraphWorkflowDefinitionRequest
{
    public GraphWorkflowGraph Graph { get; init; } = GraphWorkflowGraph.Empty;
}

// The wire graph: a field-for-field mirror of the stored graph document rather than a projection, so the mapper is a deserialize and nothing else, and a definition read
// back, edited and saved keeps every field it arrived with. There is no node or edge table — the shape is composed from the encrypted graph blob on the definition row.

/// <remarks>
///     <see cref="SchemaVersion" /> is NULLABLE, and that is the whole point: as a plain <c>int</c> an absent member
///     and an explicit <c>0</c> both arrive as 0, so the mapper could not tell "the author omitted it" from "the author
///     wrote a version this node does not speak", and normalizing both to 1 would smuggle an unsupported document past
///     the parser's version refusal. Absent means 1, and so does an explicit JSON <c>null</c> — the JSON spelling of
///     "I am not saying" rather than a version. Anything present travels verbatim and is answered by the parser.
/// </remarks>
public sealed record GraphWorkflowGraph(int? SchemaVersion, IReadOnlyList<GraphWorkflowGraphNode> Nodes, IReadOnlyList<GraphWorkflowGraphEdge> Edges)
{
    public static GraphWorkflowGraph Empty { get; } = new(1, [], []);
}

/// <summary>
///     One authored node.
/// </summary>
/// <remarks>
///     <see cref="Config" /> is the per-kind settings block carried as RAW JSON: each kind reads a different set of
///     members, and a typed union of the eight would land in the generated client as an unusable discriminated schema
///     while buying nothing — the runtime's parser refuses a member on the wrong kind, and it reads the stored
///     document rather than this projection. <see cref="Label" /> is nullable and is NOT filled in on the way out: a
///     node that named none is labelled by its key, and writing the key back would change the round-tripped bytes.
/// </remarks>
public sealed record GraphWorkflowGraphNode(
    string Key,
    string Kind,
    string? Label,
    GraphWorkflowNodePosition? Position,
    int? MaxAttempts,
    int? TimeoutSeconds,
    string? JoinPolicy,
    JsonElement? Config);

/// <summary>Where the editor drew a node. Authoring metadata the runtime reads past; absent means "lay it out on open".</summary>
public sealed record GraphWorkflowNodePosition(double X, double Y);

/// <summary>
///     One edge. <see cref="Key" /> is its identity, which is what makes PARALLEL edges expressible: two edges over the
///     same pair are legal when their keys differ. <see cref="SourceHandle" /> is authoring metadata, like a position.
/// </summary>
public sealed record GraphWorkflowGraphEdge(string Key, string From, string To, string? Label, string? SourceHandle, GraphWorkflowEdgeCondition? Condition);

/// <summary>
///     <see cref="Value" /> is a JSON scalar — string, number, boolean or null — and not a string member, and
///     <see cref="Path" /> is optional, a conditional edge leaving a <c>Condition</c> node inheriting that node's
///     <c>config.path</c>.
/// </summary>
/// <remarks>
///     A boolean round-tripped as <c>"true"</c> would type-mismatch a real boolean, the evaluator fails closed, and
///     the edge would silently never fire. <see cref="Value" /> is a NON-nullable <see cref="JsonElement" /> whose
///     <see cref="JsonValueKind.Undefined" /> means "no such member", the only shape keeping the two absences apart:
///     as a <c>JsonElement?</c>, a JSON <c>null</c> and a missing member both land as <c>null</c>, and
///     <c>{"op":"eq","value":null}</c> would come back out as the value-taking operator with no value that the parser refuses. Undefined is what the ignore condition skips.
/// </remarks>
public sealed record GraphWorkflowEdgeCondition(
    string? Path,
    string Op,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    JsonElement Value);

// Responses. Enums cross the wire as their NAMES and are typed string here; the client re-narrows them.

public sealed class GraphWorkflowDefinitionResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required GraphWorkflowGraph Graph { get; init; }

    public required string GraphHash { get; init; }

    public required int NodeCount { get; init; }

    public required int SchemaVersion { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>One row of the definition list. No graph: the node count is a column, so listing never decrypts a blob.</summary>
public sealed class GraphWorkflowDefinitionSummaryResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string? Description { get; init; }

    public required string GraphHash { get; init; }

    public required int NodeCount { get; init; }

    public required int SchemaVersion { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     One thing wrong with a graph. <see cref="Key" /> is the node or edge it belongs to, so the editor draws it on
///     the offending element rather than in a list beside the canvas; null means the failure is about the document as
///     a whole.
/// </summary>
public sealed class GraphWorkflowValidationErrorResponse
{
    public required string? Key { get; init; }

    public required string Message { get; init; }
}

/// <summary>
///     A validation report, which is why it answers 200 for anything well-formed: zero errors and five are the same
///     shape, and neither is a failure of the request that asked.
/// </summary>
/// <remarks>
///     <see cref="Warnings" /> are the same <c>(key, message)</c> shape and are NOT errors: <see cref="Valid" /> stays
///     <c>errors.length === 0</c>, a definition carrying only warnings saves and starts, and a client that ignores the
///     member is unaffected. One type for both rather than a severity member, so nothing that refuses on
///     <see cref="Errors" /> has to remember to filter first.
/// </remarks>
public sealed class ValidateGraphWorkflowDefinitionResponse
{
    public required bool Valid { get; init; }

    public required IReadOnlyList<GraphWorkflowValidationErrorResponse> Errors { get; init; }

    public required int NodeCount { get; init; }

    public required IReadOnlyList<GraphWorkflowValidationErrorResponse> Warnings { get; init; }
}

// A concrete response type per list rather than a generic envelope: NSwag builds schema ids from the CLR type name,
// and a generic would land in the generated client as an unreadable ListGraphWorkflowFeedResponseOfT.

public sealed class ListGraphWorkflowDefinitionsResponse
{
    public required IReadOnlyList<GraphWorkflowDefinitionSummaryResponse> Definitions { get; init; }
}

// Runs. The definition half above is the authoring surface; everything below is one execution of it.

/// <summary>
///     A run start. The definition comes from the route; <see cref="RequestId" /> is the caller's own idempotency key,
///     so a retry that never saw the first answer resolves to the run it already created rather than a second one.
/// </summary>
public sealed class StartGraphWorkflowRunRequest
{
    public Guid DefinitionId { get; init; }

    public Guid RequestId { get; init; }

    /// <summary>The operator's start payload, carried verbatim into the <c>Start</c> node's output document. Any JSON value; absent means none.</summary>
    public JsonElement? Input { get; init; }

    /// <summary>
    ///     The definition version the caller believed it was starting. A stale one answers 409 rather than running a
    ///     graph the caller never saw; omitted skips the check.
    /// </summary>
    public int? DefinitionVersion { get; init; }
}

public sealed class ListGraphWorkflowRunsRequest
{
    /// <summary>A <c>GraphWorkflowRunStatus</c> name. Omitted lists every status.</summary>
    public string? Status { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed class GraphWorkflowRunRequest
{
    public Guid RunId { get; init; }
}

public sealed class GraphWorkflowNodeRunRequest
{
    public Guid RunId { get; init; }

    public string NodeKey { get; init; } = string.Empty;
}

/// <summary>
///     One answer to one pause. <see cref="OperationId" /> is the caller-minted idempotency key: the same one always
///     answers with the decision it already recorded, and a different one on an answered pause is a second human act.
/// </summary>
/// <remarks>
///     <see cref="Payload" /> rides as raw JSON because it is the operator's document rather than a shape this runtime
///     declares; it must be a JSON object, and it is bounded well under the node output envelope.
/// </remarks>
public sealed class DecideGraphWorkflowNodeRunRequest
{
    public Guid RunId { get; init; }

    public string NodeKey { get; init; } = string.Empty;

    public Guid OperationId { get; init; }

    /// <summary>A <c>GraphWorkflowDecisionKind</c> name. The validator refuses anything else, so the handler can parse it.</summary>
    public string Decision { get; init; } = string.Empty;

    public string? Comment { get; init; }

    public JsonElement? Payload { get; init; }
}

/// <summary>
///     The event feed. <see cref="AfterSeq" /> is an EXCLUSIVE lower bound, so a client that stores the sequence it
///     last rendered replays nothing it already has; 0 asks for everything.
/// </summary>
public sealed class GraphWorkflowRunEventFeedRequest
{
    public Guid RunId { get; init; }

    public long AfterSeq { get; init; }
}

/// <summary>
///     What a start answers: the run id and nothing else. The run has no state worth reading yet — the dispatcher has
///     not ticked — and a full body here would invite a client to believe the statuses in it.
/// </summary>
public sealed class StartGraphWorkflowRunResponse
{
    public required Guid RunId { get; init; }
}

/// <summary>One row of the run list. Enums cross the wire as their NAMES and are typed string here; the client re-narrows them.</summary>
public sealed class GraphWorkflowRunSummaryResponse
{
    public required Guid Id { get; init; }

    public required Guid RequestId { get; init; }

    public required Guid DefinitionId { get; init; }

    public required int DefinitionVersion { get; init; }

    public required string GraphHash { get; init; }

    public required string Status { get; init; }

    public required string FailureClass { get; init; }

    public required long? CancelRequestedAtUtc { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>
///     One node run without its documents — what a run overview draws. The input and output documents are a per-node
///     read, because they are the largest thing a run stores and a graph of two hundred nodes would carry all of them.
/// </summary>
public sealed class GraphWorkflowNodeRunSummaryResponse
{
    public required Guid Id { get; init; }

    public required string NodeKey { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public required int Attempt { get; init; }

    public required string FailureClass { get; init; }

    public required string? PendingDecisionKind { get; init; }

    public required Guid? InvocationId { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     One run in full. <see cref="Output" /> is the result the succeeded <c>End</c> node resolved, written once at
///     terminalization and null until then.
/// </summary>
/// <remarks>
///     <see cref="Graph" /> is the run's OWN graph — the copy pinned when it started, not the definition's current one
///     — in the same shape <see cref="GraphWorkflowDefinitionResponse.Graph" /> carries, so a client parses both with
///     one piece of code. It is here rather than on the run SUMMARY because a list must not carry one graph per row: a
///     definition may hold up to a mebibyte of them. Without it a run view drawing the definition it names would
///     render the wrong graph for every run started before an edit, which is why a run pins a copy at all.
/// </remarks>
public sealed class GraphWorkflowRunResponse
{
    public required GraphWorkflowRunSummaryResponse Run { get; init; }

    public required IReadOnlyList<GraphWorkflowNodeRunSummaryResponse> NodeRuns { get; init; }

    public required JsonElement? Output { get; init; }

    public required GraphWorkflowGraph Graph { get; init; }
}

/// <summary>
///     One node run with its documents. Both ride as raw JSON: they are author- and model-shaped, and a typed mirror of
///     eight kinds' payloads would be a schema the runtime does not itself hold.
/// </summary>
public sealed class GraphWorkflowNodeRunResponse
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required string NodeKey { get; init; }

    public required string Kind { get; init; }

    public required string Status { get; init; }

    public required int Attempt { get; init; }

    public required string FailureClass { get; init; }

    public required string? PendingDecisionKind { get; init; }

    public required string? Error { get; init; }

    public required JsonElement? Input { get; init; }

    public required JsonElement? Output { get; init; }

    public required Guid? InvocationId { get; init; }

    public required long? StartedAtUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class GraphWorkflowRunEventResponse
{
    public required Guid Id { get; init; }

    public required long Seq { get; init; }

    public required string EventType { get; init; }

    public required string? NodeKey { get; init; }

    public required JsonElement? Detail { get; init; }

    public required long CreatedAtUtc { get; init; }
}

public sealed class ListGraphWorkflowRunsResponse
{
    public required IReadOnlyList<GraphWorkflowRunSummaryResponse> Runs { get; init; }
}

/// <summary>
///     What a decision answers with: the decision that now stands, and the CURRENT statuses of the run and of the
///     pause it answered.
/// </summary>
/// <remarks>
///     Current rather than predicted — what follows a decision is the dispatcher's work on its own clock, so a run
///     that legitimately still reads <c>WaitingForApproval</c> is reported as it is.
/// </remarks>
public sealed class GraphWorkflowDecisionResultResponse
{
    public required string Decision { get; init; }

    public required string RunStatus { get; init; }

    public required string NodeRunStatus { get; init; }
}

/// <summary>
///     One page of the event log. <see cref="ReplayTruncated" /> is observed, not inferred: the page is read one row
///     over its cap, so a client that fell behind is told it was cut off rather than handed a partial log it would
///     mistake for the whole one.
/// </summary>
public sealed class ListGraphWorkflowRunEventsResponse
{
    public required IReadOnlyList<GraphWorkflowRunEventResponse> Events { get; init; }

    public required long LastSeq { get; init; }

    public required bool ReplayTruncated { get; init; }
}
