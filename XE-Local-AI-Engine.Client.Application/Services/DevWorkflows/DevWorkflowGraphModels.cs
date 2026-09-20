namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>How a node with more than one inbound edge waits. The whole of the join semantics.</summary>
internal enum DevWorkflowJoinPolicy
{
    All,
    Any
}

/// <summary>What a Tool node does with the repository it names.</summary>
/// <remarks>
///     A CONFIG field rather than a node type, because the node types are closed and these two are the same lane
///     doing the same thing to the same workspace — one asks the project's command profile whether the result is
///     good, the other asks Dev Mode's apply gate to let it out.
/// </remarks>
internal enum DevWorkflowToolMode
{
    Validate,
    Apply
}

/// <summary>What a node can change.</summary>
/// <remarks>
///     The tokens are the tool taxonomy's <c>ToolCategory</c>, so an author writing a capability need not learn a
///     second vocabulary, but the QUESTION differs: that taxonomy asks whether a call needs an approval round-trip,
///     this asks what a node can change outside its own sandbox — they share names, not meaning. There is no
///     <c>Unknown</c>: a node declaring nothing is judged by its DERIVED effects, which are total over the node
///     types, so no node's reach is unanswerable.
/// </remarks>
internal enum DevWorkflowNodeEffect
{
    ReadLocal,
    WriteExecute,
    Orchestration,
    Network
}

/// <summary>How far out a write reaches.</summary>
/// <remarks>
///     One derived bit rather than a second taxonomy: a DevTask writes a worktree created under the node's own data
///     root, and its patch reaches the operator's repository only through the apply node a human gate stands before.
/// </remarks>
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
///     One node of the parsed graph: only what the runtime reads, since unknown properties survive in the stored
///     blob either way and this projection is not a re-serialization of it.
/// </summary>
/// <remarks>
///     <see cref="RequiredCapabilities" /> is the author's DECLARED effect set, and only the effects are kept — the
///     reason beside each is shape-checked and then left in the blob for the editor, because nothing the runtime
///     decides reads it. <see cref="ModelProfile" /> and <see cref="ReasoningEffort" /> ARE read: they pin an agent
///     node's work session, beating the bound definition's. Only their SHAPE is checked here, because a model name
///     is matched against the catalog at dispatch, so an uninstalled model does not make a graph unsaveable.
/// </remarks>
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
