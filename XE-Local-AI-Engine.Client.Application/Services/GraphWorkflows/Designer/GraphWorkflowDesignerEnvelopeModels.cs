namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Designer;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>The structured-output shape the designer model answers in: a reply and a whole graph, or null.</summary>
/// <remarks>
///     MEAI derives the schema with every member required and nullable, in declaration order, and llama.cpp's grammar emits
///     required members first; so declaration order is the wire order and follows the graph document's own, and no optional
///     member (a condition's path) can be forced behind its siblings and dropped. Configs and values stay raw JSON.
/// </remarks>
internal sealed class GraphWorkflowDesignerEnvelope
{
    public string? Reply { get; init; }

    public GraphWorkflowDesignerGraph? Proposal { get; init; }
}

internal sealed class GraphWorkflowDesignerGraph
{
    public int? SchemaVersion { get; init; }

    public GraphWorkflowDefinitionKind? Kind { get; init; }

    public GraphWorkflowDesignerChat? Chat { get; init; }

    public IReadOnlyList<GraphWorkflowDesignerNode>? Nodes { get; init; }

    public IReadOnlyList<GraphWorkflowDesignerEdge>? Edges { get; init; }
}

internal sealed class GraphWorkflowDesignerChat
{
    public bool? AcceptsAttachments { get; init; }

    public bool? RequireRerunConfirmation { get; init; }
}

internal sealed class GraphWorkflowDesignerNode
{
    public string? Key { get; init; }

    public GraphWorkflowNodeKind? Kind { get; init; }

    public string? Label { get; init; }

    public GraphWorkflowJoinPolicy? JoinPolicy { get; init; }

    public int? MaxAttempts { get; init; }

    public int? TimeoutSeconds { get; init; }

    public GraphWorkflowDesignerPosition? Position { get; init; }

    public JsonElement? Config { get; init; }
}

internal sealed class GraphWorkflowDesignerPosition
{
    public double X { get; init; }

    public double Y { get; init; }
}

internal sealed class GraphWorkflowDesignerEdge
{
    public string? Key { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public string? Label { get; init; }

    public GraphWorkflowDesignerCondition? Condition { get; init; }
}

/// <summary><see cref="Value" /> is non-nullable so a JSON <c>null</c> survives as a value and only an absent one is dropped.</summary>
internal sealed class GraphWorkflowDesignerCondition
{
    public string? Path { get; init; }

    public GraphWorkflowConditionOperator? Op { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Value { get; init; }
}
