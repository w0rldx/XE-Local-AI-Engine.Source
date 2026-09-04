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

    public int Version { get; init; }

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

// The wire graph. A field-for-field mirror of the stored graph document rather than a projection of it, so the mapper
// is a deserialize and nothing else — and so a definition read back, edited and saved keeps every field it arrived
// with. There is no node or edge table anywhere: this shape is composed from the encrypted graph blob on the
// definition row, which is the single source of routing truth.

/// <remarks>
///     <see cref="SchemaVersion" /> is NULLABLE, and that is the whole point of it: as a plain <c>int</c> an absent
///     member and an explicit <c>0</c> both arrive as 0, the mapper cannot tell "the author omitted it" from "the
///     author wrote a version this node does not speak", and normalizing both to 1 would smuggle an unsupported
///     document past the parser's version refusal. Absent means 1; anything present travels verbatim and is answered
///     by the parser.
/// </remarks>
public sealed record GraphWorkflowGraph(int? SchemaVersion, IReadOnlyList<GraphWorkflowGraphNode> Nodes, IReadOnlyList<GraphWorkflowGraphEdge> Edges)
{
    public static GraphWorkflowGraph Empty { get; } = new(1, [], []);
}

/// <summary>
///     One authored node. <see cref="Config" /> is the per-kind settings block carried as RAW JSON: each kind reads a
///     different set of members, and a typed union of the eight would land in the generated client as an unusable
///     discriminated schema while buying nothing — the runtime's parser is what refuses a member on the wrong kind,
///     and it reads the stored document rather than this projection.
///     <para>
///         <see cref="Label" /> is nullable and is NOT filled in on the way out: a node that named none is labelled by
///         its key, exactly as the parser reads it, and writing the key back into the document would change the bytes
///         a round trip stores.
///     </para>
/// </summary>
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
///     <see cref="Value" /> is a JSON scalar — string, number, boolean or null — and not a string member. A boolean
///     that round-trips as <c>"true"</c> would compare against a real boolean as a type mismatch, the evaluator fails
///     closed, and the edge would silently never fire.
///     <para>
///         <see cref="Path" /> is optional: a conditional edge leaving a <c>Condition</c> node inherits that node's
///         <c>config.path</c>.
///     </para>
///     <para>
///         <see cref="Value" /> is a NON-nullable <see cref="JsonElement" /> whose <see cref="JsonValueKind.Undefined" />
///         means "no such member", which is the only shape that keeps the two absences apart. As a
///         <c>JsonElement?</c> it could not: a JSON <c>null</c> and a missing member both land as <c>null</c>, the
///         written document drops the member either way, and <c>{"op":"eq","value":null}</c> — a comparison the
///         evaluator answers — comes back out as the one thing the parser refuses, a value-taking operator with no
///         value. Undefined is what the ignore condition below skips on the way out, so the two operators that take no
///         value (<c>Exists</c>, <c>NotExists</c>) still round-trip as the absent member they are stored as.
///     </para>
/// </summary>
public sealed record GraphWorkflowEdgeCondition(string? Path,
    string Op,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    JsonElement Value);

// Responses. Enums cross the wire as their NAMES and are typed string here; the client re-narrows them.

public sealed record GraphWorkflowDefinitionResponse(
    Guid Id,
    string Name,
    string? Description,
    GraphWorkflowGraph Graph,
    string GraphHash,
    int NodeCount,
    int SchemaVersion,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>One row of the definition list. No graph: the node count is a column, so listing never decrypts a blob.</summary>
public sealed record GraphWorkflowDefinitionSummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    string GraphHash,
    int NodeCount,
    int SchemaVersion,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     One thing wrong with a graph. <see cref="Key" /> is the node or edge it belongs to, so the editor draws it on
///     the offending element rather than in a list beside the canvas; null means the failure is about the document as
///     a whole.
/// </summary>
public sealed record GraphWorkflowValidationErrorResponse(string? Key, string Message);

/// <summary>
///     A validation report, which is why it answers 200 for anything well-formed: zero errors and five are the same
///     shape, and neither is a failure of the request that asked.
/// </summary>
public sealed record ValidateGraphWorkflowDefinitionResponse(bool Valid, IReadOnlyList<GraphWorkflowValidationErrorResponse> Errors, int NodeCount);

// A concrete response record per list rather than a generic envelope: NSwag builds schema ids from the CLR type name,
// and a generic would land in the generated client as an unreadable ListGraphWorkflowFeedResponseOfT.

public sealed record ListGraphWorkflowDefinitionsResponse(IReadOnlyList<GraphWorkflowDefinitionSummaryResponse> Definitions);
