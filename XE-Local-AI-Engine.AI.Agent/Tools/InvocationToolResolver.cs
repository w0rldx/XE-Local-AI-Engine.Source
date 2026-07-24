namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

/// <summary>
///     Shared offer-list → executable resolution used by both the single-agent
///     <see cref="Invocation.Implementation.InvocationAgentFactory" /> and the multi-agent orchestration factory.
///     Intersects the offered tools a definition carries with the executable catalogs, matched by name: built-in
///     catalog tools resolve from <see cref="IAgentToolRegistry" /> (Option A); offered names it does not satisfy
///     are then tried against <see cref="IClientLocalToolRegistry" /> (ClientLocal — server-driven <c>ClientLocal</c>
///     tools, returned already approval-wrapped when the handler opts in) and finally against
///     <see cref="IMcpToolRegistry" /> (MCP — node-local MCP tools). Names matched by none are skipped so a
///     stale or unhandled offer can never reach the agent.
///     <para>
///         Approval policy is TIGHTEN-ONLY, most-restrictive-wins, fail closed. The effective policy for a
///         resolved tool is <c>handler/registry policy OR per-agent offer policy</c>: when the offer
///         (<see cref="OfferPlaceholderAIFunction" />) requires approval, the resolved executable is wrapped in
///         <c>ApprovalRequiredAIFunction</c> unless it already is one — so a per-agent tightening of a ClientLocal,
///         built-in (spawn_subagent), or MCP tool is honored. Because the wrap is only ever ADDED and never removed, a
///         per-agent flag can never strip a handler- or MCP-enforced approval (an attempted loosen is a no-op). A
///         resolved tool whose offer carries no policy metadata (a name collision or a non-placeholder offer) fails
///         closed to requiring approval.
///     </para>
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

        // Per-agent approval policy carried on the offer placeholders, keyed by name. Most-restrictive-wins: if any
        // offer for a name requires approval, the name requires approval (covers a duplicate-name collision by tightening
        // rather than trusting the looser of the two).
        var approvalByName = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var offer in offeredTools)
        {
            if (offer is OfferPlaceholderAIFunction { Name: { Length: > 0 } name } placeholder)
            {
                approvalByName[name] = approvalByName.TryGetValue(name, out var existing)
                    ? existing || placeholder.RequiresApproval
                    : placeholder.RequiresApproval;
            }
        }

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

        // Apply the tighten-only approval override in place: wrap a resolved executable when the per-agent offer requires
        // approval and it is not already approval-wrapped. Missing policy metadata fails closed (require approval).
        for (var index = 0; index < resolved.Count; index++)
        {
            var tool = resolved[index];
            var requiresApproval = !approvalByName.TryGetValue(tool.Name, out var policy) || policy;
            if (requiresApproval && tool is not ApprovalRequiredAIFunction && tool is AIFunction executable)
            {
                resolved[index] = new ApprovalRequiredAIFunction(executable);
            }
        }

        return resolved;

        // Try ClientLocal (server-driven ClientLocal) first, then MCP (node-local MCP). Both registries key on the
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
