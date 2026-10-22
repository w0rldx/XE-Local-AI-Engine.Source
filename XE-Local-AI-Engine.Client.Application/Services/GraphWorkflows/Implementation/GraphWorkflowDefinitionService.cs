namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Validation and the store, in that order. The parse it runs is the RUNTIME's own, so a definition accepted here
///     is one that will start, and a rule added to the parser cannot be forgotten on the save path.
/// </summary>
internal sealed class GraphWorkflowDefinitionService : IGraphWorkflowDefinitionService
{
    private readonly IGraphWorkflowStore _store;

    private readonly IToolInvocationService _tools;

    private readonly INodeRuntimeSettings _runtimeSettings;

    private readonly ILocalModelProviderResolver _providers;

    private readonly IGgufModelStore _ggufModels;

    private readonly IModelTrustResolver _trust;

    private readonly IOptions<GraphWorkflowOptions> _options;

    public GraphWorkflowDefinitionService(IGraphWorkflowStore store,
        IToolInvocationService tools,
        INodeRuntimeSettings runtimeSettings,
        ILocalModelProviderResolver providers,
        IGgufModelStore ggufModels,
        IModelTrustResolver trust,
        IOptions<GraphWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(ggufModels);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _tools = tools;
        _runtimeSettings = runtimeSettings;
        _providers = providers;
        _ggufModels = ggufModels;
        _trust = trust;
        _options = options;
    }

    public async Task<GraphWorkflowValidationResult> ValidateAsync(string graphJson, CancellationToken cancellationToken = default)
    {
        try
        {
            // A graph that parsed may still have something said about it, errors or not, so an author fixing one sees both lists.
            // Warnings ride out only on this path: they never block, so the save and start paths below discard them.
            var errors = new List<GraphWorkflowValidationError>();

            // Actions count as allowed: the graph is judged without a definition, and save and start judge the flag itself.
            var graph = await ParseCollectingErrorsAsync(graphJson, allowsActions: true, errors, cancellationToken);
            var warnings = await WarningsForRuntimeAsync(graph, cancellationToken);
            return errors.Count == 0
                ? GraphWorkflowValidationResult.ValidWith(warnings)
                : new GraphWorkflowValidationResult
                {
                    Errors = errors,
                    Warnings = warnings
                };
        }
        catch (GraphWorkflowValidationException exception)
        {
            return exception.Result;
        }
    }

    public Task<GraphWorkflowDefinitionSnapshot> CreateAsync(string name,
        string? description,
        string graphJson,
        CancellationToken cancellationToken = default) =>
        CreateGatedAsync(name, description, graphJson, actionsPassTheGate: false, cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> ImportAsync(string name,
        string? description,
        string graphJson,
        CancellationToken cancellationToken = default) =>
        CreateGatedAsync(name, description, graphJson, actionsPassTheGate: true, cancellationToken);

    /// <summary>
    ///     Both creates store <c>AllowsActions</c> false (ADR 0021); only the Operator update sets it. They differ in whether
    ///     an action node passes the gate: refused for a drawn graph, admitted for an import, which the start then refuses.
    /// </summary>
    private async Task<GraphWorkflowDefinitionSnapshot> CreateGatedAsync(string name,
        string? description,
        string graphJson,
        bool actionsPassTheGate,
        CancellationToken cancellationToken)
    {
        // Node count and kind are denormalized from the SAME parse, so the list and the picker never decrypt a blob.
        var graph = await ValidateAndParseAsync(graphJson, actionsPassTheGate, cancellationToken);
        return await _store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand
            {
                DefinitionId = Guid.NewGuid(),
                Name = name,
                GraphJson = graphJson,
                NodeCount = graph.Nodes.Count,
                Kind = graph.Kind,
                Description = description
            },
            cancellationToken);
    }

    public async Task<GraphWorkflowDefinitionSnapshot> UpdateAsync(Guid definitionId,
        int expectedVersion,
        string? name,
        string? description,
        string? graphJson,
        bool? allowsActions = null,
        GraphWorkflowTriggerSource? triggerSources = null,
        CancellationToken cancellationToken = default)
    {
        // A null graph leaves the stored one alone, so the node count and kind must stay null with it: writing either for a
        // graph nobody sent would denormalize a lie the definition list then reports.
        var graph = graphJson is null
            ? null
            : await ValidateAndParseAsync(graphJson, allowsActions ?? (await _store.GetDefinitionAsync(definitionId, cancellationToken)).AllowsActions, cancellationToken);
        return await _store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand
            {
                DefinitionId = definitionId,
                ExpectedVersion = expectedVersion,
                Name = name,
                Description = description,
                GraphJson = graphJson,
                NodeCount = graph?.Nodes.Count,
                Kind = graph?.Kind,
                AllowsActions = allowsActions,
                TriggerSources = triggerSources
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.ListDefinitionsAsync(cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> GetAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _store.GetDefinitionAsync(definitionId, cancellationToken);

    public Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _store.DeleteDefinitionAsync(definitionId, cancellationToken);

    public Task<IReadOnlyList<InvocableToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        GraphWorkflowToolGate.ListAsync(_tools, _runtimeSettings, cancellationToken);

    /// <summary>The one place the option-bearing half of validation lives.</summary>
    /// <remarks>
    ///     A blank document becomes the same structured refusal every other whole-document failure produces, so
    ///     <see cref="ValidateAsync" /> can promise never to throw rather than leaking the parser's argument guard. It
    ///     answers the PARSED graph rather than a node count, because two callers want the count and one wants the
    ///     warnings and re-parsing for either would run the rule set twice. The tool gate runs once the structural rules
    ///     passed, beside the per-node errors, so one answer carries both; a config that failed to parse is no tool node.
    /// </remarks>
    private async Task<GraphWorkflowGraph> ValidateAndParseAsync(string graphJson, bool allowsActions, CancellationToken cancellationToken)
    {
        var errors = new List<GraphWorkflowValidationError>();
        var graph = await ParseCollectingErrorsAsync(graphJson, allowsActions, errors, cancellationToken);
        return errors.Count == 0
            ? graph
            : throw new GraphWorkflowValidationException(GraphWorkflowValidationResult.Invalid(errors));
    }

    /// <summary>The parse and the tool gate, adding per-node errors to <paramref name="errors" />; whole-document and structural failures still throw.</summary>
    private async Task<GraphWorkflowGraph> ParseCollectingErrorsAsync(string graphJson,
        bool allowsActions,
        List<GraphWorkflowValidationError> errors,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
        {
            throw new GraphWorkflowValidationException("A graph workflow definition needs a graph.");
        }

        var graph = GraphWorkflowGraphContract.ValidateAndParse(graphJson, _options.Value.MaxNodesPerDefinition, errors);
        errors.AddRange(await GraphWorkflowToolGate.ErrorsAsync(graph, allowsActions, _tools, _runtimeSettings, cancellationToken));
        return graph;
    }

    /// <summary>
    ///     The graph's warnings, minus the response-schema one on any Agent node whose pinned model llama-server
    ///     serves.
    /// </summary>
    /// <remarks>
    ///     That warning is about the <c>Microsoft.Extensions.AI.OpenAI</c> strict-schema rewrite, which the llama.cpp
    ///     lane does not suffer: <c>DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough</c> writes the schema
    ///     onto the request body as authored, and telling an author their bounds are dropped when the runtime enforces
    ///     them is worse than saying nothing. Here because only this seam reaches the model-to-provider map. A node
    ///     with no pin keeps its warning, and so does one mapping to another provider: the cheap error direction.
    /// </remarks>
    private async Task<IReadOnlyList<GraphWorkflowValidationError>> WarningsForRuntimeAsync(GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var suppressed = new HashSet<GraphWorkflowValidationError>();
        foreach (var warning in graph.ResponseSchemaWarnings)
        {
            if (warning.Key is not { } nodeKey || !graph.Nodes.TryGetValue(nodeKey, out var node))
            {
                continue;
            }

            var model = node.Config switch
            {
                GraphWorkflowAgentConfig { Model: { } agentModel } => agentModel,
                GraphWorkflowLlmCallConfig { Model: { } llmModel } => llmModel,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            if (await ServedByLlamaServerAsync(model, cancellationToken))
            {
                suppressed.Add(warning);
            }
        }

        var warnings = suppressed.Count == 0 ? graph.Warnings : [.. graph.Warnings.Where(warning => !suppressed.Contains(warning))];
        var runtime = (IReadOnlyList<GraphWorkflowValidationError>)
        [
            .. await UninstalledModelWarningsAsync(graph, cancellationToken),
            .. await LocalDataEdgeWarningsAsync(graph, cancellationToken)
        ];
        return runtime.Count == 0 ? warnings : [.. warnings, .. runtime];
    }

    /// <summary>One warning per model node whose OWN pin would be refused the output of a direct local-data Tool predecessor.</summary>
    /// <remarks>
    ///     Mirrors the executor's local-data gate on the definition, so it warns conservatively: an <c>Any</c> join whose
    ///     local-data branch fails would run. An unpinned node is skipped like the unattended warning skips it; the runtime
    ///     gate is authoritative. A trust lookup that faults reads as local and says nothing.
    /// </remarks>
    private async Task<IReadOnlyList<GraphWorkflowValidationError>> LocalDataEdgeWarningsAsync(GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var warnings = new List<GraphWorkflowValidationError>();
        foreach (var node in graph.Nodes.Values.OrderBy(static node => node.NodeKey, StringComparer.Ordinal))
        {
            var model = node.Config switch
            {
                GraphWorkflowAgentConfig { Model: { } agentModel, IncludeUpstreamOutputs: true } => agentModel,
                GraphWorkflowLlmCallConfig { Model: { } llmModel, InputBindings.Count: > 0 } => llmModel,
                GraphWorkflowDecisionModelConfig { Model: { } decisionModel, InputBindings.Count: > 0 } when !IsNativeDecisionNode(node.Config) => decisionModel,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            var source = graph.InboundEdges(node.NodeKey)
                              .Select(edge => graph.Nodes[edge.From])
                              .OrderBy(static predecessor => predecessor.NodeKey, StringComparer.Ordinal)
                              .FirstOrDefault(GraphWorkflowToolGate.ReadsLocalData);
            if (source is null
                || await IsLocalAsync(model, cancellationToken)
                || !await CloudLocalDataGate.WithholdsAsync(leavesNode: true, await _trust.ResolveCloudGrantsAsync(model, cancellationToken), _runtimeSettings, cancellationToken))
            {
                continue;
            }

            warnings.Add(new GraphWorkflowValidationError(node.NodeKey,
                $"Node '{node.NodeKey}' pins model '{model}', which is not a node-local model, and reads Tool node '{source.NodeKey}' "
                + $"('{((GraphWorkflowToolConfig)source.Config).ToolName}'), which returns node-local data, so a run will refuse this node. Turn on "
                + "'Let cloud models read local data' in Node Settings → Privacy & updates, or grant local data on the model's external connection when it has one, to allow it."));
        }

        return warnings;
    }

    /// <summary>One warning per model-running node pinned to a model a run of that node would refuse.</summary>
    /// <remarks>
    ///     Mirrors the executor's admission: an LLM call or decision pin is asked through <see cref="NodeLocalModelGate" />; a cloud (or
    ///     unresolved) one is refused only while unattended cloud runs are off, so the warning names that switch. An Agent run takes any
    ///     node-local model its provider serves, so its pin warns only when it routes to llama-server and no GGUF is installed (an Ollama
    ///     model never warns), and it refuses a cloud one in its own words. A lookup that faults says nothing, so validation never throws.
    /// </remarks>
    private async Task<IReadOnlyList<GraphWorkflowValidationError>> UninstalledModelWarningsAsync(GraphWorkflowGraph graph, CancellationToken cancellationToken)
    {
        var warnings = new List<GraphWorkflowValidationError>();
        foreach (var node in graph.Nodes.Values.OrderBy(static node => node.NodeKey, StringComparer.Ordinal))
        {
            if (node.Config is GraphWorkflowAgentConfig { Model: { } agentModel } && !string.IsNullOrWhiteSpace(agentModel))
            {
                if (await IsLocalAsync(agentModel, cancellationToken) && await IsMissingLlamaServerModelAsync(agentModel, cancellationToken))
                {
                    warnings.Add(NotInstalledWarning(node.NodeKey, agentModel));
                }

                continue;
            }

            var model = node.Config switch
            {
                GraphWorkflowLlmCallConfig { Model: { } llmModel } => llmModel,
                GraphWorkflowDecisionModelConfig { Model: { } decisionModel } => decisionModel,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            if (await IsLocalAsync(model, cancellationToken))
            {
                if (await IsNotInstalledAsync(model, cancellationToken))
                {
                    warnings.Add(NotInstalledWarning(node.NodeKey, model, IsNativeDecisionNode(node.Config) ? "decision" : "chat"));
                }
            }
            else if (await CloudUnattendedGate.WithholdsAsync(leavesNode: true, await _trust.ResolveCloudGrantsAsync(model, cancellationToken), _runtimeSettings, cancellationToken))
            {
                warnings.Add(new GraphWorkflowValidationError(node.NodeKey,
                    $"Node '{node.NodeKey}' pins model '{model}', which is not a node-local model, so a run will refuse this node. Turn on "
                    + "'Let cloud models run unattended' in Node Settings → Privacy & updates, or grant unattended runs on the model's external connection when it has one, to allow it."));
            }
        }

        return warnings;
    }

    private static GraphWorkflowValidationError NotInstalledWarning(string nodeKey, string model, string modelKind = "chat") =>
        new(nodeKey,
            $"Node '{nodeKey}' pins model '{model}', which is not an installed node-managed GGUF {modelKind} model, so a run will refuse this node. "
            + "Install the model or pin another one.");

    /// <summary>A <c>DecisionModel</c> node answered by a native decision model rather than a chat model.</summary>
    private static bool IsNativeDecisionNode(GraphWorkflowNodeConfig config) =>
        config is GraphWorkflowDecisionModelConfig { Provider: SystemOneDecisionProvider.ProviderName };

    /// <summary>Whether <paramref name="model" /> routes to llama-server with no GGUF installed under its name, answering <see langword="false" /> when a lookup cannot say.</summary>
    private async Task<bool> IsMissingLlamaServerModelAsync(string model, CancellationToken cancellationToken)
    {
        if (!await ServedByLlamaServerAsync(model, cancellationToken))
        {
            return false;
        }

        try
        {
            return !await _ggufModels.ExistsAsync(model.Trim(), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Same contract as ServedByLlamaServerAsync: a warning is never worth failing a validation over.
            return false;
        }
    }

    /// <summary>Whether trust resolves <paramref name="model" /> as node-local, answering <see langword="true" /> when the lookup cannot say so the installed check still runs.</summary>
    private async Task<bool> IsLocalAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            return await _trust.ResolveAsync(model, cancellationToken) == ModelTrustLocality.Local;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Same contract as ServedByLlamaServerAsync: a warning is never worth failing a validation over.
            return true;
        }
    }

    /// <summary>Whether the runtime would refuse <paramref name="model" /> as not installed, answering <see langword="false" /> when the lookup cannot say.</summary>
    private async Task<bool> IsNotInstalledAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            return !await NodeLocalModelGate.IsInstalledNodeLocalLlamaModelAsync(model, _ggufModels, _trust, _providers, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Same contract as ServedByLlamaServerAsync: a warning is never worth failing a validation over.
            return false;
        }
    }

    /// <summary>
    ///     Whether llama-server serves <paramref name="model" />, answering <see langword="false" /> when the lookup
    ///     cannot say.
    /// </summary>
    /// <remarks>
    ///     The resolver opens a scope, takes a map read lease and reads the store, so it can fail for reasons that
    ///     have nothing to do with the graph being validated — and validation is a warning-only, never-blocking path.
    ///     Letting a store fault escape would turn an editor's probe into a 500, while answering
    ///     <see langword="false" /> keeps the warning, the same cheap-error direction an unpinned node already takes.
    /// </remarks>
    private async Task<bool> ServedByLlamaServerAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            var provider = await _providers.ResolveProviderNameForModelAsync(model, cancellationToken);
            return string.Equals(provider, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Broad by intent: every failure mode of the lookup has the same right answer here, and the caller's
            // contract is that a warning is never worth failing a validation over. Cancellation still propagates.
            return false;
        }
    }
}
