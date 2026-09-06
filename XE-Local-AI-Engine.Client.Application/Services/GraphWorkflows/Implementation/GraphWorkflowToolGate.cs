namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     Ruling D6's gate over a parsed graph: every <c>Tool</c> node must name a tool
///     <see cref="IToolInvocationService" /> would actually invoke. Save time and run start ask the SAME question of
///     the same catalog, so a definition accepted at save is refused at start only because the envelope tightened in
///     between — which is the case the run-start check exists for.
/// </summary>
internal static class GraphWorkflowToolGate
{
    /// <summary>
    ///     One error per offending <c>Tool</c> node, keyed by NODE KEY so the editor draws it on that node. Empty for a
    ///     graph whose tools are all invocable, which is what lets both callers treat "no errors" as the whole answer.
    ///     <para>
    ///         A tool outside the envelope is an ERROR, never a warning: a workflow node runs unattended, so a write,
    ///         execute or approval-gated tool has nobody to ask (ADR 0006).
    ///     </para>
    /// </summary>
    public static async Task<IReadOnlyList<GraphWorkflowValidationError>> ErrorsAsync(GraphWorkflowGraph graph,
        IToolInvocationService tools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tools);

        // A graph with no Tool node asks the catalog nothing. The catalog read opens a scope over the custom-tool
        // store on every call by design, and every save and every start of every other shape would pay for it.
        if (graph.ToolNodeNames.Count == 0)
        {
            return [];
        }

        var invocable = await tools.ListInvocableToolsAsync(cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(invocable.Select(static tool => tool.Name), StringComparer.Ordinal);
        return
        [
            .. graph.Nodes.Values.OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                    .Select(static node => (node.NodeKey, ToolName: (node.Config as GraphWorkflowToolConfig)?.ToolName))
                    .Where(entry => entry.ToolName is { } toolName && !names.Contains(toolName))
                    .Select(static entry => new GraphWorkflowValidationError(entry.NodeKey, Refusal(entry.ToolName!)))
        ];
    }

    /// <summary>
    ///     Names the tool and the rule, never the catalog: listing what IS invocable here would go stale against the
    ///     tools endpoint the picker reads, and the two would disagree in front of the author.
    /// </summary>
    private static string Refusal(string toolName) =>
        $"Tool '{toolName}' is not one this node may run from a Tool node: the envelope is the built-in read-local tools that need no approval, "
        + "so a write, execute or approval-gated tool is refused here rather than warned about.";
}
