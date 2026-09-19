namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>How a node with more than one inbound edge waits. The whole of the join semantics.</summary>
internal enum DevWorkflowJoinPolicy
{
    All,
    Any
}

/// <summary>
///     What a Tool node does with the repository it names. A CONFIG field rather than a node type, because the seven
///     types are closed (Y6) and these two are the same lane doing the same thing to the same workspace — one asks the
///     project's command profile whether the result is good, the other asks Dev Mode's apply gate to let it out.
/// </summary>
internal enum DevWorkflowToolMode
{
    Validate,
    Apply
}

/// <summary>
///     What a node can change. The tokens are the tool taxonomy's — <c>ToolCategory</c> — because an author writing a
///     capability should not have to learn a second vocabulary for the same idea, but the QUESTION is a different one:
///     the chat taxonomy asks whether a call needs an approval round-trip, and this asks what a node can change outside
///     its own sandbox. They share names, not meaning.
///     <para>
///         There is no <c>Unknown</c>. A node declaring nothing is judged by its DERIVED effects, which are total over
///         the seven node types, so there is never a node whose reach is unanswerable.
///     </para>
/// </summary>
internal enum DevWorkflowNodeEffect
{
    ReadLocal,
    WriteExecute,
    Orchestration,
    Network
}

/// <summary>
///     How far out a write reaches. One derived bit rather than a second taxonomy: a DevTask writes a worktree created
///     under the node's own data root, and its patch reaches the operator's repository only through the apply node a
///     human gate already stands in front of.
/// </summary>
internal enum DevWorkflowEffectScope
{
    Sandbox,
    Repository
}

/// <summary>The decomposition template a node expands into. All four fields are load-bearing.</summary>
internal sealed class DevWorkflowMaterialization
{
    public required string TemplateNodeKey { get; init; }

    public required DevWorkflowArtifactKind ArtifactKind { get; init; }

    public required string JoinNodeKey { get; init; }

    public required int MaxChildren { get; init; }
}

/// <summary>
///     One node of the parsed graph. Only what the runtime reads; unknown properties survive in the stored blob either
///     way, because this projection is not a re-serialization of it.
///     <para>
///         <see cref="RequiredCapabilities" /> is the author's DECLARED effect set. Only the effects are kept: the
///         reason written beside each one is checked here (a token the vocabulary knows, a string, at most 200
///         characters) and then left in the blob for the editor to render, because nothing the runtime decides reads
///         it.
///     </para>
///     <para>
///         <see cref="ModelProfile" /> and <see cref="ReasoningEffort" /> ARE read: an agent node's work session is
///         created with them as its own pins, beating the bound agent definition's. Only their SHAPE is checked here —
///         a model name is matched against this node's catalog at dispatch, exactly as an agent definition's pin is,
///         so a graph does not become unsaveable because a model was uninstalled after it was authored.
///     </para>
/// </summary>
internal sealed class DevWorkflowGraphNode
{
    public required string NodeKey { get; init; }

    public required DevWorkflowNodeType NodeType { get; init; }

    public required string Label { get; init; }

    public required Guid? AgentDefinitionId { get; init; }

    public required string? AgentSeedSlug { get; init; }

    public required string? Instructions { get; init; }

    public required IReadOnlyList<string> ValidationCommandIds { get; init; }

    public required DevWorkflowJoinPolicy JoinPolicy { get; init; }

    public required int MaxAttempts { get; init; }

    public required int RetryDelaySeconds { get; init; }

    public required int? NodeTimeoutSeconds { get; init; }

    public required string? RetryTarget { get; init; }

    public required DevWorkflowMaterialization? Materialization { get; init; }

    public required DevWorkflowToolMode ToolMode { get; init; }

    public required string? ModelProfile { get; init; }

    public required string? ReasoningEffort { get; init; }

    public required IReadOnlySet<DevWorkflowNodeEffect> RequiredCapabilities { get; init; }

    public required int? MaxLoopIterations { get; init; }
}

internal sealed record DevWorkflowGraphEdge(string From, string To, DevWorkflowCondition? Condition)
{
    public override string ToString() =>
        $"'{From}' → '{To}'";
}
