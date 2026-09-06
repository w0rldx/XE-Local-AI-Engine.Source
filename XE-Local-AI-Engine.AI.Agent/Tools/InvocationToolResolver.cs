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
    // The reserved custom-tool name prefix (mirrors CustomToolValidation.ToolNamePrefix, which lives in Client.Application
    // and cannot be referenced from this AI.Agent-layer resolver). Only offered names carrying it are ever put to the
    // custom-tool catalog, so a non-custom offer never triggers a store read. Keep the two literals in sync.
    private const string CustomToolNamePrefix = "custom__";

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

        return ResolveCore(offeredTools, toolRegistry, clientLocalToolRegistry, mcpToolRegistry, preResolvedCustom: null, logger);
    }

    /// <summary>
    ///     The offer → executable resolution EXTENDED with the node-local custom tool catalog. Used by the single-agent and
    ///     orchestration invocation factories and by the explicitly trusted agentic MCP root path. Delegate MCP and
    ///     spawned-child paths stay on <see cref="Resolve" /> and cannot resolve custom tools. The custom names are pre-resolved through
    ///     <paramref name="customToolCatalog" /> (a DbContext-backed, async store read) BEFORE the synchronous core runs, so
    ///     no <c>.Result</c>/<c>.Wait()</c> ever blocks the thread pool. Each custom executable the catalog returns is ALREADY
    ///     wrapped in <c>ApprovalRequiredAIFunction</c> (its authoritative approval floor), so the core's tighten-only wrap
    ///     is a no-op on it.
    /// </summary>
    public static async Task<IList<AITool>> ResolveAsync(IReadOnlyList<AITool> offeredTools,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        ICustomToolCatalog customToolCatalog,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offeredTools);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(clientLocalToolRegistry);
        ArgumentNullException.ThrowIfNull(mcpToolRegistry);
        ArgumentNullException.ThrowIfNull(customToolCatalog);
        ArgumentNullException.ThrowIfNull(logger);

        if (offeredTools.Count == 0)
        {
            return [];
        }

        // Pre-resolve the offered custom__ names via the async catalog. A disabled node kill-switch or an unknown name
        // leaves the name out of the returned dictionary and it simply stays unresolved (the core then skips + warns it,
        // like any other unmatched offer).
        var customNames = offeredTools
                          .Select(static offer => offer.Name)
                          .Where(static name => !string.IsNullOrWhiteSpace(name)
                                                && name.StartsWith(CustomToolNamePrefix, StringComparison.Ordinal))
                          .Distinct(StringComparer.Ordinal)
                          .ToArray();

        // ONE catalog round trip for the whole offer: the catalog reads the store once and matches every requested name
        // against that single snapshot. Still a live read per resolution — no cache. Passing null when the offer carries
        // no custom name keeps the common path free of both a catalog call and an empty-dictionary allocation.
        var preResolvedCustom = customNames.Length == 0
            ? null
            : await customToolCatalog.TryResolveManyAsync(customNames, cancellationToken).ConfigureAwait(false);

        return ResolveCore(offeredTools, toolRegistry, clientLocalToolRegistry, mcpToolRegistry, preResolvedCustom, logger);
    }

    private static IList<AITool> ResolveCore(IReadOnlyList<AITool> offeredTools,
        IAgentToolRegistry toolRegistry,
        IClientLocalToolRegistry clientLocalToolRegistry,
        IMcpToolRegistry mcpToolRegistry,
        IReadOnlyDictionary<string, AITool>? preResolvedCustom,
        ILogger logger)
    {
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

        // Try ClientLocal (server-driven ClientLocal) first, then MCP (node-local MCP), then the pre-resolved node-local
        // custom tools. The three name spaces are disjoint (custom names carry the reserved custom__ prefix), so the first
        // match wins. The custom executable is already approval-wrapped by the catalog, so the tighten-only wrap above sees
        // an ApprovalRequiredAIFunction and leaves it as-is.
        AITool? ResolveDynamicTool(string name)
        {
            if (clientLocalToolRegistry.TryResolve(name, out var clientLocalTool))
            {
                return clientLocalTool;
            }

            if (mcpToolRegistry.TryResolve(name, out var mcpTool))
            {
                return mcpTool;
            }

            return preResolvedCustom is not null && preResolvedCustom.TryGetValue(name, out var customTool) ? customTool : null;
        }
    }
}
