namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

/// <summary>
///     Projects the store's snapshots onto the wire contracts. Entities never reach an endpoint: their text columns
///     are encrypted at rest, so a mapper reading one would hand the operator ciphertext.
/// </summary>
/// <remarks>
///     The graph crosses in BOTH directions, deliberately as a deserialize-and-reserialize of the same field list
///     rather than a projection, so a stored member these wire types do not enumerate is DROPPED on the way out. That
///     is safe only because every member the runtime reads IS enumerated, and a document written to a schema version
///     this node does not speak is refused by the parser rather than trimmed here. Per-kind node settings and an edge
///     condition's value ride as raw <see cref="JsonElement" />: stringified, a boolean would type-mismatch a real one.
/// </remarks>
internal static class GraphWorkflowContractMapper
{
    /// <summary>
    ///     The stored document's own shape: camelCase, nulls omitted, so a round trip through here still parses.
    /// </summary>
    /// <remarks>
    ///     An edge condition's <c>value</c> is the one member this must NOT drop when it is null: it carries its own
    ///     ignore condition, and an absent value is a <see cref="JsonValueKind.Undefined" /> element rather than a
    ///     null, so an explicit <c>null</c> survives both directions while an absent one stays absent.
    /// </remarks>
    private static readonly JsonSerializerOptions GraphOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    ///     The stored graph document as the wire shape, normalizing only two things: an absent <c>schemaVersion</c>
    ///     reads as 1, and an absent node or edge list reads as empty rather than null.
    /// </summary>
    /// <remarks>
    ///     Everything else — labels, positions, per-kind config, condition values — is handed over exactly as stored.
    ///     Unreadable text THROWS, unlike <see cref="ToDocument" />, because a graph was parsed before it was ever
    ///     stored: there is no supported route to a corrupt one, so a 500 with a log is the honest answer, where an
    ///     empty canvas drawn beside a real <c>nodeCount</c> would report the corruption as a graph nobody drew.
    /// </remarks>
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
    ///     The wire graph as the document that gets stored.
    /// </summary>
    /// <remarks>
    ///     An ABSENT schema version is written as 1 — the version an editor that never sends the member is drawing —
    ///     and a PRESENT integer travels verbatim, 0 and 2 included, so the parser answers it with the version refusal
    ///     instead of this mapper quietly making it supported. An explicit JSON <c>null</c> and an absent member BOTH
    ///     mean 1, deliberately: null is the JSON spelling of "I am not saying", not of a version, and it smuggles
    ///     nothing past the parser, since every version this node refuses is an integer.
    /// </remarks>
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
        return new GraphWorkflowDefinitionResponse
        {
            Id = value.Id,
            Name = value.Name,
            Description = value.Description,
            Graph = ToWireGraph(value.GraphJson),
            GraphHash = value.GraphHash,
            NodeCount = value.NodeCount,
            SchemaVersion = value.SchemaVersion,
            Kind = value.Kind.ToString(),
            Version = value.Version,
            CreatedAtUtc = value.CreatedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc
        };
    }

    public static GraphWorkflowDefinitionSummaryResponse ToResponse(this GraphWorkflowDefinitionSummary value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowDefinitionSummaryResponse
        {
            Id = value.Id,
            Name = value.Name,
            Description = value.Description,
            GraphHash = value.GraphHash,
            NodeCount = value.NodeCount,
            SchemaVersion = value.SchemaVersion,
            Kind = value.Kind.ToString(),
            Version = value.Version,
            CreatedAtUtc = value.CreatedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc
        };
    }

    public static GraphWorkflowRunSummaryResponse ToResponse(this GraphWorkflowRunSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowRunSummaryResponse
        {
            Id = value.Id,
            RequestId = value.RequestId,
            DefinitionId = value.DefinitionId,
            DefinitionVersion = value.DefinitionVersion,
            GraphHash = value.GraphHash,
            Status = value.Status.ToString(),
            FailureClass = value.FailureClass.ToString(),
            CancelRequestedAtUtc = value.CancelRequestedAtUtc,
            StartedAtUtc = value.StartedAtUtc,
            CompletedAtUtc = value.CompletedAtUtc,
            CreatedAtUtc = value.CreatedAtUtc
        };
    }

    public static GraphWorkflowConversationRunResponse ToResponse(this GraphWorkflowChatBoundRun value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new GraphWorkflowConversationRunResponse
        {
            Run = value.Run.ToResponse(),
            DefinitionId = value.Run.DefinitionId,
            DefinitionName = value.DefinitionName,
            TriggerMessageId = value.Run.TriggerMessageId,
            PendingInput = value.PendingInput is { } input ? new GraphWorkflowPendingInputResponse { NodeKey = input.NodeKey, Prompt = input.Prompt } : null,
            Steerable = value.SteerableNodeKey is { } nodeKey ? new GraphWorkflowSteerableNodeResponse { NodeKey = nodeKey } : null
        };
    }

    public static GraphWorkflowRunResponse ToResponse(this GraphWorkflowRunDetail value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowRunResponse
        {
            Run = value.Run.ToResponse(),
            NodeRuns = [.. value.NodeRuns.Select(ToSummaryResponse)],
            Output = ToDocument(value.Run.OutputJson),
            // The run's PINNED blob, through the same projection a definition read uses: the definition it names may
            // have been edited, or deleted, since — and the node runs below belong to this graph, not to that one.
            Graph = ToWireGraph(value.Run.GraphJson)
        };
    }

    public static GraphWorkflowNodeRunSummaryResponse ToSummaryResponse(this GraphWorkflowNodeRunSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowNodeRunSummaryResponse
        {
            Id = value.Id,
            NodeKey = value.NodeKey,
            Kind = value.Kind.ToString(),
            Status = value.Status.ToString(),
            Attempt = value.Attempt,
            FailureClass = value.FailureClass.ToString(),
            PendingDecisionKind = value.PendingDecisionKind?.ToString(),
            InvocationId = value.InvocationId,
            StartedAtUtc = value.StartedAtUtc,
            CompletedAtUtc = value.CompletedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc
        };
    }

    public static GraphWorkflowNodeRunResponse ToResponse(this GraphWorkflowNodeRunSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowNodeRunResponse
        {
            Id = value.Id,
            RunId = value.RunId,
            NodeKey = value.NodeKey,
            Kind = value.Kind.ToString(),
            Status = value.Status.ToString(),
            Attempt = value.Attempt,
            FailureClass = value.FailureClass.ToString(),
            PendingDecisionKind = value.PendingDecisionKind?.ToString(),
            Error = value.Error,
            Input = ToDocument(value.InputJson),
            Output = ToDocument(value.OutputJson),
            InvocationId = value.InvocationId,
            StartedAtUtc = value.StartedAtUtc,
            CompletedAtUtc = value.CompletedAtUtc,
            UpdatedAtUtc = value.UpdatedAtUtc,
            Steering = [.. value.Steering.Select(static entry => new GraphWorkflowSteeringEntryResponse
            {
                OperationId = entry.OperationId,
                Message = entry.Message,
                AtUtc = entry.AtUtc,
                Attempt = entry.Attempt,
                Applied = entry.Applied
            })]
        };
    }

    public static GraphWorkflowRunEventResponse ToResponse(this GraphWorkflowRunEventSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new GraphWorkflowRunEventResponse { Id = value.Id, Seq = value.Seq, EventType = value.EventType, NodeKey = value.NodeKey, Detail = ToDocument(value.DetailJson), CreatedAtUtc = value.CreatedAtUtc };
    }

    /// <summary>
    ///     A stored document as raw JSON on the wire, or null when there is none.
    /// </summary>
    /// <remarks>
    ///     Unreadable text answers null rather than throwing. These blobs are written by the runtime, so a document
    ///     that will not parse is a bug somewhere upstream — and answering 500 to a node-run read would take away the
    ///     one page an operator would diagnose it from.
    /// </remarks>
    private static JsonElement? ToDocument(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
