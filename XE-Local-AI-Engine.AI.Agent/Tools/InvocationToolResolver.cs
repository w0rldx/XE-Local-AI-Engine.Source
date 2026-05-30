namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
///     Shared offer-list → executable resolution used by both the single-agent
///     <see cref="Invocation.Implementation.InvocationAgentFactory" /> and the multi-agent orchestration factory.
///     Intersects the offered tools a definition carries with the executable catalogs, matched by name: built-in
///     catalog tools resolve from <see cref="IAgentToolRegistry" /> (Option A); offered names it does not satisfy
///     are then tried against <see cref="IClientLocalToolRegistry" /> (Option B — server-driven <c>ClientLocal</c>
///     tools, returned already approval-wrapped when the handler opts in) and finally against
///     <see cref="IMcpToolRegistry" /> (Option C — node-local MCP tools). Names matched by none are skipped so a
///     stale or unhandled offer can never reach the agent.
/// </summary>
internal static class InvocationToolResolver
{
    public static IList<AITool> Resolve(IReadOnlyList<AITool> offeredTools,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(offeredTools);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(clientLocalToolRegistry);
        ArgumentNullException.ThrowIfNull(mcpToolRegistry);
        ArgumentNullException.ThrowIfNull(logger);

        if (offeredTools.Count == 0)
        {
            return [];
        }

        var offeredNames = offeredTools
                           .Select(static tool => tool.Name)
                           .Where(static name => !string.IsNullOrWhiteSpace(name))
                           .ToHashSet(StringComparer.Ordinal);

        var resolved = toolRegistry.GetLocalChatTools()
                                   .Where(tool => offeredNames.Contains(tool.Name))
                                   .ToList();

        var catalogNames = resolved.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        resolved.AddRange(offeredNames.Where(name => !catalogNames.Contains(name))
                                      .Select(ResolveDynamicTool)
                                      .OfType<AITool>());

        var skipped = offeredNames.Count - resolved.Count;
        if (skipped > 0)
        {
            // An offered tool with no in-process catalog match and no client-local or MCP handler is a
            // misconfiguration: the server or node advertised a tool this node cannot execute. Warn so it is
            // observable, then drop the offer rather than letting it reach the agent.
            logger.LogWarning("Skipped {SkippedCount} offered tool(s) with no registered executable (no catalog, client-local, or MCP match).", skipped);
        }

        return resolved;

        // Try Option B (server-driven ClientLocal) first, then Option C (node-local MCP). Both registries key on the
        // offered name and a name cannot legitimately exist in both, so the first match wins.
        AITool? ResolveDynamicTool(string name)
        {
            if (clientLocalToolRegistry.TryResolve(name, out var clientLocalTool))
            {
                return clientLocalTool;
            }

            return mcpToolRegistry.TryResolve(name, out var mcpTool) ? mcpTool : null;
        }
    }
}
