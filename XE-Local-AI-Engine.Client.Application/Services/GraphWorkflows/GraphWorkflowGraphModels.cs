namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>Where the editor drew a node. Authoring metadata the runtime never reads.</summary>
internal sealed class GraphWorkflowPosition
{
    public required double X { get; init; }

    public required double Y { get; init; }
}

/// <summary>
///     The per-kind settings of one node. Discriminated rather than a bag, because a member that does nothing where it
///     is written is a definition saying something the runtime will not do — so the parser refuses it there.
/// </summary>
internal abstract record GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowStartConfig(JsonElement? InputSchema, JsonElement? DefaultInput) : GraphWorkflowNodeConfig;

/// <summary><see cref="Model" /> and <see cref="ReasoningEffort" /> are the two dispatch pins.</summary>
/// <remarks>
///     The model name travels as written and is matched against this node's catalog when the run starts, exactly as
///     an agent definition's own pin is, so a graph does not become unsaveable because a model was uninstalled after
///     it was authored. The effort IS checked here: its vocabulary is closed and cannot go stale between authoring
///     and a run.
/// </remarks>
internal sealed record GraphWorkflowAgentConfig(
    Guid? AgentDefinitionId,
    string Instructions,
    string? Model,
    string? ReasoningEffort,
    JsonElement? ResponseJsonSchema,
    bool IncludeUpstreamOutputs) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowToolConfig(string ToolName, JsonElement? Arguments, IReadOnlyDictionary<string, string> ArgumentBindings) : GraphWorkflowNodeConfig;

/// <summary>
///     <see cref="Path" /> is the node-level DEFAULT dot path its own out-edges inherit when their condition omits one.
///     Optional: an author may write the path on every branch instead.
/// </summary>
internal sealed record GraphWorkflowConditionConfig(string? Path) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowPauseConfig(string Prompt, IReadOnlyList<GraphWorkflowDecisionKind> AllowedDecisions, bool RequireComment) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowEndConfig(string Outcome, string? ResultPath) : GraphWorkflowNodeConfig;

/// <summary>The config of a kind that has none — <c>Parallel</c> and <c>Join</c> are shape, not settings.</summary>
internal sealed record GraphWorkflowEmptyConfig : GraphWorkflowNodeConfig;

/// <summary>
///     One node of the parsed graph. Only what the runtime reads: unknown properties survive in the stored blob either
///     way — this projection is not a re-serialization of it.
/// </summary>
internal sealed class GraphWorkflowGraphNode
{
    public required string NodeKey { get; init; }

    public required GraphWorkflowNodeKind Kind { get; init; }

    public required string Label { get; init; }

    public required GraphWorkflowJoinPolicy JoinPolicy { get; init; }

    public required int MaxAttempts { get; init; }

    public required int? TimeoutSeconds { get; init; }

    public required GraphWorkflowPosition? Position { get; init; }

    public required GraphWorkflowNodeConfig Config { get; init; }
}

/// <summary>
///     One edge. <see cref="Key" /> is its identity — required and unique, which is what makes PARALLEL edges
///     expressible: two edges over the same pair are legal when at most one of them is unconditional.
/// </summary>
/// <remarks><see cref="Label" /> is the named outcome a node's output document reports as its <c>branch</c>.</remarks>
internal sealed record GraphWorkflowGraphEdge(string Key, string From, string To, string? Label, GraphWorkflowCondition? Condition)
{
    public override string ToString() =>
        $"'{Key}' ('{From}' → '{To}')";
}
