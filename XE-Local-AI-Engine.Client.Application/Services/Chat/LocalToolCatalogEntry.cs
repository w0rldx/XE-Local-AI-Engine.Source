namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     A single entry in the node's full tool catalog: a built-in tool or an enabled MCP tool. This is the
///     model-agnostic catalog the management/agent-form UI consumes — it lists every tool that exists on the node,
///     independent of which model is active (capability gating lives only in <c>GetOfferedTools</c>).
///     <see cref="Source" /> is <c>"builtin"</c> for in-process catalog tools (time/calculator) and
///     <c>"mcp:{serverSlug}"</c> for a tool discovered from a registered MCP server, so the UI can group/badge tools by
///     their originating server.
/// </summary>
public sealed record LocalToolCatalogEntry
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required bool RequiresApproval { get; init; }

    public required string Source { get; init; }

    /// <summary>
    ///     The tool's risk class, carried from its definition-site <c>Category</c>. The UI badges it so an
    ///     operator can see a tool's class, and the node-default approval policy reads it to compute the effective
    ///     approval. Defaults to <see cref="ToolCategory.Unknown" /> (fail-closed) for any entry that did not declare one.
    /// </summary>
    public ToolCategory Category { get; init; } = ToolCategory.Unknown;

    /// <summary>
    ///     Set only for a node-local custom tool: whether it runs a verbatim, operator-authored invocation
    ///     (<c>CustomToolMode.Fixed</c>) rather than one the model parameterizes. The catalog response feeds it to
    ///     <c>SessionApprovalEligibility.IsToolEligible</c> — a Fixed custom tool can carry a session-scoped approval, a
    ///     Parameterized one is once-or-deny. <see langword="false" /> for every non-custom entry.
    /// </summary>
    public bool IsFixedCustomTool { get; init; }
}
