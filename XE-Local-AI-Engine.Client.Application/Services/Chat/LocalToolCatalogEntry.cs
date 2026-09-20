namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     A single entry in the node's full tool catalog: a built-in tool or an enabled MCP tool.
/// </summary>
/// <remarks>
///     This is the model-agnostic catalog the management and agent-form UI consumes, listing every tool on the node
///     whatever the active model, since capability gating lives only in <c>GetOfferedTools</c>.
///     <see cref="Source" /> is <c>"builtin"</c> or <c>"mcp:{serverSlug}"</c>, so the UI can group by server.
/// </remarks>
public sealed record LocalToolCatalogEntry
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required bool RequiresApproval { get; init; }

    public required string Source { get; init; }

    /// <summary>
    ///     The tool's risk class, carried from its definition-site <c>Category</c>: the UI badges it and the
    ///     node-default approval policy reads it. An entry that declared none is fail-closed
    ///     <see cref="ToolCategory.Unknown" />.
    /// </summary>
    public ToolCategory Category { get; init; } = ToolCategory.Unknown;

    /// <summary>
    ///     Set only for a node-local custom tool: whether it runs a verbatim, operator-authored invocation rather than
    ///     one the model parameterizes; <see langword="false" /> for every non-custom entry.
    /// </summary>
    /// <remarks>
    ///     The catalog response feeds it to <c>SessionApprovalEligibility.IsToolEligible</c>: a Fixed custom tool can
    ///     carry a session-scoped approval, a Parameterized one is once-or-deny.
    /// </remarks>
    public bool IsFixedCustomTool { get; init; }
}
