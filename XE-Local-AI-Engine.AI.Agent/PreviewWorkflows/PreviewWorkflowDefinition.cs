namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

/// <summary>
///     Provider-agnostic description of an Open Canvas (Preview) workflow graph. This is the ONE graph contract the
///     runner consumes; Application/Client mirror it (zod schema + Client.Models) and map their stored/inline graph
///     onto it. NO Microsoft.Agents.AI types appear here (invariant: MAF stays inside the runner/session).
///
///     === GRAPH SCHEMA (mirror this in the Application zod schema + the Client.Models DTOs) ===
///     A workflow is a STRICTLY LINEAR chain (in-degree/out-degree ≤ 1 per node — enforced by definition validation):
///         Start → Agent → [Agent…] → (Debug | Pause)* → End
///     Nodes:
///       - <see cref="PreviewNodeKind.Start" />  : carries the seed user text (<see cref="PreviewWorkflowDefinition.StartText" />).
///       - <see cref="PreviewNodeKind.Agent" />  : a model call. Carries Id, Label, Instructions, ModelId,
///                                                 optional ModelProfile + ReasoningEffort (see <see cref="PreviewAgentNode" />).
///       - <see cref="PreviewNodeKind.Debug" />  : a tap — emits the upstream payload as a side event, forwards unchanged.
///       - <see cref="PreviewNodeKind.Pause" />  : halts the run; surfaces the upstream output; resumes on a continue signal.
///       - <see cref="PreviewNodeKind.End" />    : terminal output.
///     Edges: directed <see cref="PreviewWorkflowEdge" /> { SourceId, TargetId }.
///     Run output is NEVER part of this contract — it streams as <see cref="PreviewWorkflowUpdate" /> events only.
/// </summary>
public sealed record PreviewWorkflowDefinition
{
    /// <summary>The seed user text emitted by the Start node into the first agent.</summary>
    public required string StartText { get; init; }

    /// <summary>All nodes in the graph (each carries its <see cref="PreviewWorkflowNode.Kind" /> discriminator).</summary>
    public required IReadOnlyList<PreviewWorkflowNode> Nodes { get; init; }

    /// <summary>Directed edges wiring the linear chain.</summary>
    public required IReadOnlyList<PreviewWorkflowEdge> Edges { get; init; }
}

/// <summary>The block kinds the canvas supports (basic variant — switch/structured-output/tools deferred).</summary>
public enum PreviewNodeKind
{
    Start,
    Agent,
    Debug,
    Pause,
    End
}

/// <summary>
///     Base node. The <see cref="Kind" /> discriminator selects the concrete shape; only <see cref="PreviewAgentNode" />
///     carries extra fields. Start/Debug/Pause/End nodes need only an <see cref="Id" /> (Start text lives on the
///     definition).
/// </summary>
public record PreviewWorkflowNode
{
    /// <summary>Stable node id (also used as the MAF executor id inside the runner).</summary>
    public required string Id { get; init; }

    /// <summary>Discriminator selecting how the runner maps this node onto a MAF executor.</summary>
    public required PreviewNodeKind Kind { get; init; }
}

/// <summary>
///     An Agent (model-call) node. Instructions + model selection are the privacy-sensitive payload (encrypted at rest
///     by the persistence layer). The runner builds a <c>ChatClientAgent</c> over the caller-supplied node-local
///     client using these.
/// </summary>
public sealed record PreviewAgentNode : PreviewWorkflowNode
{
    /// <summary>Operator-facing label for the node (display only).</summary>
    public required string Label { get; init; }

    /// <summary>System instructions for this agent (becomes <c>ChatClientAgent</c> instructions).</summary>
    public required string Instructions { get; init; }

    /// <summary>The node-local model id (e.g. an Ollama model name) this agent runs on.</summary>
    public required string ModelId { get; init; }

    /// <summary>Optional model profile/family hint carried through to the local model selection.</summary>
    public string? ModelProfile { get; init; }

    /// <summary>Optional reasoning effort hint (e.g. "low"/"medium"/"high"/"on"); null = provider default.</summary>
    public string? ReasoningEffort { get; init; }
}

/// <summary>A directed edge in the workflow graph.</summary>
public sealed record PreviewWorkflowEdge
{
    public required string SourceId { get; init; }

    public required string TargetId { get; init; }
}
