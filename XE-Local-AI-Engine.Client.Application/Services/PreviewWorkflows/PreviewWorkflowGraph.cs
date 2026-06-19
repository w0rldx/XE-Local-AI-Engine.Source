namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

using System.Text.Json.Serialization;

/// <summary>
///     The ONE Client-side Open Canvas (Preview) workflow graph contract. This is the wire shape the
///     FastEndpoints Command/Query records carry, the shape the React zod schema mirrors, and the shape
///     serialized into the encrypted <c>CanvasWorkflowRecord.GraphJson</c> blob. It is mapped onto the
///     <c>PreviewWorkflowDefinition</c> (.AI.Agent) just before a run — three explicit serializations of one
///     contract: stored blob ↔ this model ↔ runner DTO.
///     Field names are deliberately clean and stable; do not rename without updating the React zod schema and the
///     stored-blob format together.
/// </summary>
public sealed record PreviewWorkflowGraph
{
    /// <summary>The seed user text the Start node emits into the first agent.</summary>
    public string StartText { get; init; } = string.Empty;

    /// <summary>All nodes in the graph; each carries its <see cref="PreviewWorkflowGraphNode.Kind" /> discriminator.</summary>
    public IReadOnlyList<PreviewWorkflowGraphNode> Nodes { get; init; } = [];

    /// <summary>Directed edges wiring the strictly linear chain.</summary>
    public IReadOnlyList<PreviewWorkflowGraphEdge> Edges { get; init; } = [];
}

/// <summary>The block kinds the canvas supports (basic variant — switch/structured-output/tools deferred).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PreviewWorkflowNodeKind>))]
public enum PreviewWorkflowNodeKind
{
    Start,
    Agent,
    Debug,
    Pause,
    End
}

/// <summary>
///     A single node. <see cref="Kind" /> selects the shape: only an <see cref="PreviewWorkflowNodeKind.Agent" /> node
///     populates the agent fields (label/instructions/model/profile/reasoning); the others need only <see cref="Id" />
///     (Start text lives on the graph). Modeled as one flat record (not a polymorphic hierarchy) so the wire/zod
///     contract stays a single object shape.
/// </summary>
public sealed record PreviewWorkflowGraphNode
{
    /// <summary>Stable node id (also the MAF executor id inside the runner).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Discriminator selecting how the node maps onto a runner executor.</summary>
    public PreviewWorkflowNodeKind Kind { get; init; }

    /// <summary>Operator-facing label (Agent nodes; display only).</summary>
    public string? Label { get; init; }

    /// <summary>System instructions (Agent nodes). Privacy-sensitive; encrypted at rest by the persistence layer.</summary>
    public string? Instructions { get; init; }

    /// <summary>Node-local model id this agent runs on (Agent nodes).</summary>
    public string? Model { get; init; }

    /// <summary>Optional model profile/family hint (Agent nodes).</summary>
    public string? ModelProfile { get; init; }

    /// <summary>Optional reasoning effort hint, e.g. "low"/"medium"/"high"/"on" (Agent nodes); null = provider default.</summary>
    public string? ReasoningEffort { get; init; }
}

/// <summary>A directed edge in the workflow graph.</summary>
public sealed record PreviewWorkflowGraphEdge
{
    public string SourceId { get; init; } = string.Empty;

    public string TargetId { get; init; } = string.Empty;
}
