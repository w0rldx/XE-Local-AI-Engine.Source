namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

/// <summary>
///     Provider-agnostic graph contract mirrored by the Application zod schema and Client DTOs; MAF types stay inside
///     the runner/session. Definition validation restricts workflows to a linear chain:
///     Start → Agent → [Agent…] → (Debug | Pause)* → End
///     Run output streams as <see cref="PreviewWorkflowUpdate" /> events rather than entering this contract.
/// </summary>
public sealed record PreviewWorkflowDefinition
{
    public required string StartText { get; init; }

    public required IReadOnlyList<PreviewWorkflowNode> Nodes { get; init; }

    public required IReadOnlyList<PreviewWorkflowEdge> Edges { get; init; }
}

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

    public required PreviewNodeKind Kind { get; init; }
}

/// <summary>
///     An Agent (model-call) node. Instructions + model selection are the privacy-sensitive payload (encrypted at rest
///     by the persistence layer). The runner builds a <c>ChatClientAgent</c> over the caller-supplied node-local
///     client using these.
/// </summary>
public sealed record PreviewAgentNode : PreviewWorkflowNode
{
    public required string Label { get; init; }

    public required string Instructions { get; init; }

    public required string ModelId { get; init; }

    public string? ModelProfile { get; init; }

    /// <summary>Optional reasoning effort hint (e.g. "low"/"medium"/"high"/"on"); null = provider default.</summary>
    public string? ReasoningEffort { get; init; }
}

public sealed record PreviewWorkflowEdge
{
    public required string SourceId { get; init; }

    public required string TargetId { get; init; }
}
