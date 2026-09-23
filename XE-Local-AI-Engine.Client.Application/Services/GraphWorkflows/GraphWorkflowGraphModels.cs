namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Models;
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
    bool IncludeUpstreamOutputs) : GraphWorkflowNodeConfig
{
    /// <summary>Whether a chat-bound run posts this node's answer into the conversation. Read by the chat publisher, never by routing.</summary>
    public bool PublishToChat { get; init; }

    /// <summary>Whether the run's chat attachments reach this node's turn. Only legal when the graph's <c>chat.acceptsAttachments</c> is on.</summary>
    public bool IncludeAttachments { get; init; }
}

internal sealed record GraphWorkflowLlmCallConfig : GraphWorkflowNodeConfig
{
    public required string? Model { get; init; }

    public required string? SystemPrompt { get; init; }

    public required string Prompt { get; init; }

    public required IReadOnlyDictionary<string, string> InputBindings { get; init; }

    public required string? ReasoningEffort { get; init; }

    public required JsonElement? ResponseJsonSchema { get; init; }

    public required SamplingOptions? SamplingOptions { get; init; }

    /// <summary>See <see cref="GraphWorkflowAgentConfig.PublishToChat" />.</summary>
    public bool PublishToChat { get; init; }

    /// <summary>See <see cref="GraphWorkflowAgentConfig.IncludeAttachments" />.</summary>
    public bool IncludeAttachments { get; init; }
}

/// <summary>
///     A parked wait on the chat user's next message. <see cref="Prompt" /> is what the chat surface shows while the run
///     waits; the answer is the user's text, recorded as <c>{ decision: "Answer", text }</c>.
/// </summary>
internal sealed record GraphWorkflowChatInputConfig : GraphWorkflowNodeConfig
{
    public required string Prompt { get; init; }
}

/// <summary>
///     A classifier node: <see cref="Question" /> asked of a decision provider, answered with exactly one of
///     <see cref="Labels" />. <see cref="Provider" /> is a closed vocabulary; <c>llm</c> lowers to an LLM call.
/// </summary>
internal sealed record GraphWorkflowDecisionModelConfig : GraphWorkflowNodeConfig
{
    public required string Question { get; init; }

    public required IReadOnlyList<string> Labels { get; init; }

    public required string Provider { get; init; }

    public required string? Model { get; init; }

    public required IReadOnlyDictionary<string, string> InputBindings { get; init; }
}

/// <summary>The graph-level <c>chat</c> block. Only a <c>Chat</c> graph may declare one; a Chat graph that omits it reads these defaults.</summary>
internal sealed record GraphWorkflowChatSettings
{
    public static GraphWorkflowChatSettings Default { get; } = new() { AcceptsAttachments = false, RequireRerunConfirmation = true };

    public required bool AcceptsAttachments { get; init; }

    public required bool RequireRerunConfirmation { get; init; }
}

internal sealed record GraphWorkflowToolConfig(string ToolName, JsonElement? Arguments, IReadOnlyDictionary<string, string> ArgumentBindings) : GraphWorkflowNodeConfig;

/// <summary>
///     <see cref="Path" /> is the node-level DEFAULT dot path its own out-edges inherit when their condition omits one.
///     Optional: an author may write the path on every branch instead.
/// </summary>
internal sealed record GraphWorkflowConditionConfig(string? Path) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowPauseConfig(string Prompt, IReadOnlyList<GraphWorkflowDecisionKind> AllowedDecisions, bool RequireComment) : GraphWorkflowNodeConfig;

internal sealed record GraphWorkflowEndConfig(string Outcome, string? ResultPath) : GraphWorkflowNodeConfig
{
    /// <summary>Defaults to true in a <c>Chat</c> graph and false otherwise. See <see cref="GraphWorkflowAgentConfig.PublishToChat" />.</summary>
    public bool PublishToChat { get; init; }
}

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
