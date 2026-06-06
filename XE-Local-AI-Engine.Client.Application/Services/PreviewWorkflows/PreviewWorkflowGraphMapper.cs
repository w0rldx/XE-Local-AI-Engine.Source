namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

/// <summary>
///     The single mapper layer for the one graph contract (invariant #6): JSON blob ↔ <see cref="PreviewWorkflowGraph" />
///     (Client model) ↔ <see cref="PreviewWorkflowDefinition" /> (.AI.Agent runner DTO). The persisted
///     <c>GraphJson</c> is a JSON serialization of the Client model; the execution path deserializes it (saved run) or
///     takes a Client model inline (unsaved run) and maps it onto the runner DTO.
/// </summary>
public static class PreviewWorkflowGraphMapper
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // Keep null agent fields out of the stored blob and never reorder; the Client model is the source of truth.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Serializes a Client graph to the JSON string stored (then encrypted) in <c>CanvasWorkflowRecord.GraphJson</c>.</summary>
    public static string Serialize(PreviewWorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return JsonSerializer.Serialize(graph, SerializerOptions);
    }

    /// <summary>
    ///     Deserializes a stored <c>GraphJson</c> string back into the Client graph. Throws
    ///     <see cref="PreviewWorkflowGraphFormatException" /> when the stored blob is malformed (defensive — the blob is
    ///     written only by <see cref="Serialize" />, but a corrupt/old row must not crash the run path).
    /// </summary>
    public static PreviewWorkflowGraph Deserialize(string graphJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphJson);

        PreviewWorkflowGraph? graph;
        try
        {
            graph = JsonSerializer.Deserialize<PreviewWorkflowGraph>(graphJson, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new PreviewWorkflowGraphFormatException("The stored workflow graph is not valid JSON.", exception);
        }

        return graph ?? throw new PreviewWorkflowGraphFormatException("The stored workflow graph deserialized to null.");
    }

    /// <summary>
    ///     Maps a validated Client graph onto the runner DTO. Caller MUST validate first (the runner trusts a linear,
    ///     well-formed definition). Agent nodes carry their privacy-sensitive instructions + model through unchanged.
    /// </summary>
    public static PreviewWorkflowDefinition ToDefinition(PreviewWorkflowGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var nodes = graph.Nodes.Select(MapNode).ToList();
        var edges = graph.Edges
                         .Select(static e => new PreviewWorkflowEdge { SourceId = e.SourceId, TargetId = e.TargetId })
                         .ToList();

        return new PreviewWorkflowDefinition
        {
            StartText = graph.StartText,
            Nodes = nodes,
            Edges = edges
        };
    }

    private static PreviewWorkflowNode MapNode(PreviewWorkflowGraphNode node)
    {
        if (node.Kind != PreviewWorkflowNodeKind.Agent)
        {
            return new PreviewWorkflowNode { Id = node.Id, Kind = MapKind(node.Kind) };
        }

        // Validation guarantees these are present for Agent nodes; coalesce defensively to satisfy the required init.
        return new PreviewAgentNode
        {
            Id = node.Id,
            Kind = PreviewNodeKind.Agent,
            Label = node.Label ?? node.Id,
            Instructions = node.Instructions ?? string.Empty,
            ModelId = node.Model ?? string.Empty,
            ModelProfile = node.ModelProfile,
            ReasoningEffort = node.ReasoningEffort
        };
    }

    private static PreviewNodeKind MapKind(PreviewWorkflowNodeKind kind)
    {
        return kind switch
        {
            PreviewWorkflowNodeKind.Start => PreviewNodeKind.Start,
            PreviewWorkflowNodeKind.Agent => PreviewNodeKind.Agent,
            PreviewWorkflowNodeKind.Debug => PreviewNodeKind.Debug,
            PreviewWorkflowNodeKind.Pause => PreviewNodeKind.Pause,
            PreviewWorkflowNodeKind.End => PreviewNodeKind.End,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown preview workflow node kind.")
        };
    }
}

/// <summary>Thrown when a stored workflow graph blob cannot be deserialized into the Client graph model.</summary>
public sealed class PreviewWorkflowGraphFormatException : Exception
{
    public PreviewWorkflowGraphFormatException(string message) : base(message)
    {
    }

    public PreviewWorkflowGraphFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
