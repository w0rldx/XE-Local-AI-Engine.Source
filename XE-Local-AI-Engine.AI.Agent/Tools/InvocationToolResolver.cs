namespace XE_Local_AI_Engine.AI.Agent.Tools;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

/// <summary>
///     Shared offer-list to executable resolution, used by both the single-agent
///     <see cref="Invocation.Implementation.InvocationAgentFactory" /> and the orchestration factory.
/// </summary>
/// <remarks>
///     Intersects a definition's offered tools with the executable catalogs by name, trying
///     <see cref="IAgentToolRegistry" />, then <see cref="IClientLocalToolRegistry" />, then
///     <see cref="IMcpToolRegistry" />; a name matched by none is skipped, so a stale offer never reaches the agent.
///     Approval is TIGHTEN-ONLY, most-restrictive-wins and fail-closed: the effective policy is the registry's OR the
///     per-agent offer's, the wrap is only ever ADDED, and an offer with no policy metadata requires approval.
/// </remarks>
internal static class InvocationToolResolver
{
    // The reserved custom-tool name prefix, mirroring CustomToolValidation.ToolNamePrefix in Client.Application, which
    // this layer cannot reference; only names carrying it reach the catalog, so a non-custom offer reads no store.
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
    ///     The offer-to-executable resolution EXTENDED with the node-local custom tool catalog.
    /// </summary>
    /// <remarks>
    ///     Used by the single-agent and orchestration factories and by the explicitly trusted agentic MCP root path;
    ///     delegate MCP and spawned-child paths stay on <see cref="Resolve" /> and cannot resolve custom tools. Custom
    ///     names are pre-resolved through <paramref name="customToolCatalog" />, a DbContext-backed async store read,
    ///     BEFORE the synchronous core runs, so nothing ever blocks the thread pool. Each custom executable arrives
    ///     ALREADY wrapped in <c>ApprovalRequiredAIFunction</c>, so the core's tighten-only wrap is a no-op on it.
    /// </remarks>
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

        // Pre-resolve the offered custom names via the async catalog. A disabled kill-switch or an unknown name leaves
        // it out of the dictionary and unresolved, so the core skips and warns it like any other unmatched offer.
        var customNames = offeredTools
                          .Select(static offer => offer.Name)
                          .Where(static name => !string.IsNullOrWhiteSpace(name)
                                                && name.StartsWith(CustomToolNamePrefix, StringComparison.Ordinal))
                          .Distinct(StringComparer.Ordinal)
                          .ToArray();

        // ONE catalog round trip for the whole offer, still a live read per resolution and not a cache. Null when the
        // offer carries no custom name keeps the common path free of a catalog call and an empty dictionary.
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

        // Per-agent approval policy carried on the offer placeholders, keyed by name. Most-restrictive-wins, so a
        // duplicate-name collision tightens rather than trusting the looser of the two.
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
            // An offered tool with no catalog, client-local or MCP match is a misconfiguration: something advertised a
            // tool this node cannot execute. Warn so it is observable, then drop it rather than pass it to the agent.
            logger.LogWarning("Skipped {SkippedCount} offered tool(s) with no registered executable (no catalog, client-local, or MCP match).", skipped);
        }

        // Apply the tighten-only approval override in place: wrap a resolved executable when the per-agent offer
        // requires approval and it is not already wrapped. Missing policy metadata fails closed.
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

        // ClientLocal first, then MCP, then the pre-resolved custom tools; the three name spaces are disjoint, so the
        // first match wins. A custom executable is already approval-wrapped, so the wrap above leaves it as-is.
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
