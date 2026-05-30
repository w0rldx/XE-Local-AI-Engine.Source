namespace XE_Local_AI_Engine.Client.Services.Chat;

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
}
