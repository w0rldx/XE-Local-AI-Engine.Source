namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Projects the store's snapshots onto the wire contracts. Entities never reach an endpoint: their text columns are
///     encrypted at rest, so a mapper reading one would hand the operator ciphertext.
///     <para>
///         The graph crosses in BOTH directions here, and deliberately as a deserialize-and-reserialize of the same
///         field list rather than a projection: what survives a round trip is exactly the members these wire records
///         enumerate, and a member the stored document carries that they do not is DROPPED on the way back out. That
///         is safe only because every member the runtime reads is enumerated — a document written to a schema version
///         this node does not speak is refused by the parser rather than quietly trimmed here. Per-kind node settings
///         and an edge condition's value ride as raw <see cref="JsonElement" />, which is what keeps a boolean a
///         boolean — stringified, it would compare against a real boolean as a type mismatch, the evaluator fails
///         closed, and the edge would silently never fire.
///     </para>
/// </summary>
internal static class GraphWorkflowContractMapper
{
    /// <summary>
    ///     The stored document's own shape: camelCase, nulls omitted, so a round trip through here still parses. An
    ///     edge condition's <c>value</c> is the one member this must NOT drop when it is null: it carries its own
    ///     ignore condition, and an absent value is a <see cref="JsonValueKind.Undefined" /> element rather than a
    ///     null, so an explicit <c>null</c> survives both directions while an absent one stays absent.
    /// </summary>
    private static readonly JsonSerializerOptions GraphOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     The stored graph document as the wire shape. Only two things are normalized: an absent
    ///     <c>schemaVersion</c> reads as 1, and an absent node or edge list reads as empty rather than null. Everything
    ///     else — labels, positions, per-kind config, condition values — is handed over exactly as it was stored.
    /// </summary>
    public static GraphWorkflowGraph ToWireGraph(string graphJson)
    {
        var graph = JsonSerializer.Deserialize<GraphWorkflowGraph>(graphJson, GraphOptions) ?? GraphWorkflowGraph.Empty;
        return graph with
        {
            SchemaVersion = graph.SchemaVersion ?? 1,
            Nodes = graph.Nodes ?? [],
            Edges = graph.Edges ?? []
        };
    }

    /// <summary>
    ///     The wire graph as the document that gets stored. An ABSENT schema version is written as 1 — the version an
    ///     editor that never sends the member is drawing — and a PRESENT one travels verbatim, 0 and 2 included, so
    ///     the parser answers it with the version refusal instead of this mapper quietly making it supported.
    /// </summary>
    public static string ToGraphJson(GraphWorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph with
            {
                SchemaVersion = graph.SchemaVersion ?? 1,
                Nodes = graph.Nodes ?? [],
                Edges = graph.Edges ?? []
            },
            GraphOptions);
    }

    public static GraphWorkflowDefinitionResponse ToResponse(this GraphWorkflowDefinitionSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowDefinitionResponse(value.Id,
            value.Name,
            value.Description,
            ToWireGraph(value.GraphJson),
            value.GraphHash,
            value.NodeCount,
            value.SchemaVersion,
            value.Version,
            value.CreatedAtUtc,
            value.UpdatedAtUtc);
    }

    public static GraphWorkflowDefinitionSummaryResponse ToResponse(this GraphWorkflowDefinitionSummary value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowDefinitionSummaryResponse(value.Id,
            value.Name,
            value.Description,
            value.GraphHash,
            value.NodeCount,
            value.SchemaVersion,
            value.Version,
            value.CreatedAtUtc,
            value.UpdatedAtUtc);
    }
}
