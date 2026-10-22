namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;

/// <summary>
///     The tool gate over a parsed graph: every <c>Tool</c> node must name a tool
///     <see cref="IToolInvocationService" /> would actually invoke, or be the one allow-listed <c>web_fetch</c> exception.
/// </summary>
/// <remarks>
///     Save time and run start ask the SAME question of the same catalog, so a definition accepted at save is refused
///     at start only because the envelope tightened in between. The web exception (ADR 0017 decision 6, on ADR 0006's
///     basis): <c>web_fetch</c> only while web access is on and only with a non-empty <c>allowedUrls</c>, the consent
///     an unattended node cannot ask for; a literal <c>url</c> is checked here, a bound one at dispatch.
///     <c>web_search</c> never. Why: docs/wiki/21-graph-workflows.md §4.3.
/// </remarks>
internal static class GraphWorkflowToolGate
{
    /// <summary>
    ///     What a Tool node may name: the invocation envelope, plus <c>web_fetch</c> flagged as needing an allow-list
    ///     while web access is on. The ONE list the gate and the editor's picker both read.
    /// </summary>
    public static async Task<IReadOnlyList<InvocableToolDescriptor>> ListAsync(IToolInvocationService tools,
        INodeRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        var invocable = await tools.ListInvocableToolsAsync(cancellationToken);
        return await runtimeSettings.GetWebAccessEnabledAsync(cancellationToken)
            ?
            [
                .. invocable,
                new InvocableToolDescriptor
                {
                    Name = WebFetchToolDefinition.ToolName,
                    Description = WebFetchToolDefinition.Description,
                    ParameterSchema = WebFetchToolDefinition.ParameterSchema,
                    RequiresAllowedUrls = true
                }
            ]
            : invocable;
    }

    /// <summary>
    ///     Whether <paramref name="node" /> is a <c>Tool</c> node whose tool returns node-local data: the set the chat
    ///     local-data gate withholds from a cloud model (<see cref="LocalToolOfferProvider.LocalDataToolNames" />).
    /// </summary>
    public static bool ReadsLocalData(GraphWorkflowGraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        return node.Config is GraphWorkflowToolConfig config && LocalToolOfferProvider.LocalDataToolNames.Contains(config.ToolName);
    }

    /// <summary>The first DIRECT predecessor, in node-key order, that fed <paramref name="inputJson" /> node-local data, or <see langword="null" />.</summary>
    /// <remarks>
    ///     Read off the composed document's <c>upstream</c> keys, which hold only SATISFIED edges, so a local-data Tool
    ///     that failed on an <c>Any</c> join taints nothing. Why: docs/wiki/21-graph-workflows.md §4.3.
    /// </remarks>
    public static GraphWorkflowGraphNode? LocalDataSource(GraphWorkflowGraph graph, string inputJson)
    {
        ArgumentNullException.ThrowIfNull(graph);

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("upstream", out var upstream)
                || upstream.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return upstream.EnumerateObject()
                           .Select(static property => property.Name)
                           .Order(StringComparer.Ordinal)
                           .Select(key => graph.Nodes.GetValueOrDefault(key))
                           .FirstOrDefault(static node => node is not null && ReadsLocalData(node));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One error per offending <c>Tool</c> node, keyed by NODE KEY so the editor draws it on that node.</summary>
    /// <remarks>
    ///     Empty for a graph whose tools are all invocable, which is what lets both callers treat "no errors" as the
    ///     whole answer. A tool outside the envelope is an ERROR, never a warning: a workflow node runs unattended, so
    ///     a write, execute or approval-gated tool has nobody to ask (ADR 0006). An action tool is an error too unless
    ///     <paramref name="allowsActions" />, the definition's own flag (ADR 0021).
    /// </remarks>
    public static async Task<IReadOnlyList<GraphWorkflowValidationError>> ErrorsAsync(GraphWorkflowGraph graph,
        bool allowsActions,
        IToolInvocationService tools,
        INodeRuntimeSettings runtimeSettings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        // A graph with no Tool node asks the catalog nothing. The catalog read opens a scope over the custom-tool
        // store on every call by design, and every save and every start of every other shape would pay for it.
        if (graph.ToolNodeNames.Count == 0)
        {
            return [];
        }

        var invocable = (await ListAsync(tools, runtimeSettings, cancellationToken)).ToDictionary(static tool => tool.Name, static tool => tool.IsAction, StringComparer.Ordinal);
        return
        [
            .. graph.Nodes.Values.OrderBy(static node => node.NodeKey, StringComparer.Ordinal)
                    .Select(node => new
                    {
                        node.NodeKey,
                        Error = node.Config is GraphWorkflowToolConfig config ? ErrorFor(config, invocable, allowsActions) : null
                    })
                    .Where(static entry => entry.Error is not null)
                    .Select(static entry => new GraphWorkflowValidationError(entry.NodeKey, entry.Error!))
        ];
    }

    /// <summary>The one node's error, or <see langword="null" />. <paramref name="invocable" /> maps each runnable name to whether it is an action.</summary>
    private static string? ErrorFor(GraphWorkflowToolConfig config, Dictionary<string, bool> invocable, bool allowsActions)
    {
        if (string.Equals(config.ToolName, WebSearchToolDefinition.ToolName, StringComparison.Ordinal))
        {
            return $"Tool '{WebSearchToolDefinition.ToolName}' is never available to a Tool node: a graph reaches the web only through "
                   + $"'{WebFetchToolDefinition.ToolName}' against the node's allowed links.";
        }

        if (!invocable.TryGetValue(config.ToolName, out var isAction))
        {
            return string.Equals(config.ToolName, WebFetchToolDefinition.ToolName, StringComparison.Ordinal)
                ? $"Tool '{WebFetchToolDefinition.ToolName}' needs web access, which is off on this node."
                : Refusal(config.ToolName);
        }

        if (isAction && !allowsActions)
        {
            return $"Tool '{config.ToolName}' is an action with a side effect, so this node needs 'Allow actions' on this definition.";
        }

        if (!string.Equals(config.ToolName, WebFetchToolDefinition.ToolName, StringComparison.Ordinal))
        {
            return config.AllowedUrls.Count == 0
                ? null
                : $"Only a '{WebFetchToolDefinition.ToolName}' Tool node reads 'allowedUrls'; tool '{config.ToolName}' does not.";
        }

        return AllowListError(config);
    }

    /// <summary>
    ///     The <c>web_fetch</c> node's own rules: a non-empty list of absolute http(s) prefixes, and a literal
    ///     <c>url</c>, when the author typed one, inside it. An allow-list entry is the author's own text, so naming it
    ///     echoes nothing a run produced.
    /// </summary>
    private static string? AllowListError(GraphWorkflowToolConfig config)
    {
        if (config.AllowedUrls.Count == 0)
        {
            return $"A '{WebFetchToolDefinition.ToolName}' Tool node needs at least one allowed link in 'allowedUrls'; "
                   + "the list is the consent an unattended node cannot ask for.";
        }

        var invalid = config.AllowedUrls.FirstOrDefault(static entry => !IsHttpUrl(entry, out _));
        if (invalid is not null)
        {
            return $"The allowed link '{invalid}' is not an absolute http or https URL.";
        }

        if (config.Arguments is { ValueKind: JsonValueKind.Object } literals
            && literals.TryGetProperty("url", out var url)
            && (url.ValueKind != JsonValueKind.String || !IsHttpUrl(url.GetString(), out var literal) || !WebFetchService.IsAllowed(literal, config.AllowedUrls)))
        {
            return $"The literal 'url' argument of this '{WebFetchToolDefinition.ToolName}' node is not under any of its allowed links.";
        }

        return null;
    }

    private static bool IsHttpUrl(string? value, [NotNullWhen(true)] out Uri? url) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out url) && url.Scheme is "http" or "https";

    /// <summary>
    ///     Names the tool and the rule, never the catalog: listing what IS invocable here would go stale against the
    ///     tools endpoint the picker reads, and the two would disagree in front of the author.
    /// </summary>
    private static string Refusal(string toolName) =>
        $"Tool '{toolName}' is not one this node may run from a Tool node: the envelope is the built-in read-local tools that need no approval, "
        + "so a write, execute or approval-gated tool is refused here rather than warned about.";
}
