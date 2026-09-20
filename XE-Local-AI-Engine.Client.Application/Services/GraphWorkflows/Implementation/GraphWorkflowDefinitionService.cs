namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Validation and the store, in that order. The parse it runs is the RUNTIME's own, so a definition accepted here
///     is one that will start, and a rule added to the parser cannot be forgotten on the save path.
/// </summary>
internal sealed class GraphWorkflowDefinitionService : IGraphWorkflowDefinitionService
{
    private readonly IGraphWorkflowStore _store;

    private readonly IToolInvocationService _tools;

    private readonly ILocalModelProviderResolver _providers;

    private readonly IOptions<GraphWorkflowOptions> _options;

    public GraphWorkflowDefinitionService(
        IGraphWorkflowStore store,
        IToolInvocationService tools,
        ILocalModelProviderResolver providers,
        IOptions<GraphWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _tools = tools;
        _providers = providers;
        _options = options;
    }

    public async Task<GraphWorkflowValidationResult> ValidateAsync(string graphJson, CancellationToken cancellationToken = default)
    {
        try
        {
            // A graph that routes may still have something said about it. Warnings ride out only on this path: they
            // never block, so the save and start paths below discard them rather than pretend to act on them.
            var graph = await ValidateAndParseAsync(graphJson, cancellationToken);
            return GraphWorkflowValidationResult.ValidWith(await WarningsForRuntimeAsync(graph, cancellationToken));
        }
        catch (GraphWorkflowValidationException exception)
        {
            return exception.Result;
        }
    }

    public async Task<GraphWorkflowDefinitionSnapshot> CreateAsync(string name,
        string? description,
        string graphJson,
        CancellationToken cancellationToken = default)
    {
        var nodeCount = (await ValidateAndParseAsync(graphJson, cancellationToken)).Nodes.Count;
        return await _store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand { DefinitionId = Guid.NewGuid(), Name = name, GraphJson = graphJson, NodeCount = nodeCount, Description = description },
                               cancellationToken);
    }

    public async Task<GraphWorkflowDefinitionSnapshot> UpdateAsync(Guid definitionId,
        int expectedVersion,
        string? name,
        string? description,
        string? graphJson,
        CancellationToken cancellationToken = default)
    {
        // A null graph leaves the stored one alone, so the node count must stay null with it: writing a count for a
        // graph nobody sent would denormalize a lie the definition list then reports.
        int? nodeCount = graphJson is null ? null : (await ValidateAndParseAsync(graphJson, cancellationToken)).Nodes.Count;
        return await _store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand { DefinitionId = definitionId, ExpectedVersion = expectedVersion, Name = name, Description = description, GraphJson = graphJson, NodeCount = nodeCount },
                               cancellationToken);
    }

    public Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.ListDefinitionsAsync(cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> GetAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _store.GetDefinitionAsync(definitionId, cancellationToken);

    public Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        _store.DeleteDefinitionAsync(definitionId, cancellationToken);

    /// <summary>The one place the option-bearing half of validation lives.</summary>
    /// <remarks>
    ///     A blank document becomes the same structured refusal every other whole-document failure produces, so
    ///     <see cref="ValidateAsync" /> can promise never to throw rather than leaking the parser's argument guard. It
    ///     answers the PARSED graph rather than a node count, because two callers want the count and one wants the
    ///     warnings and re-parsing for either would run the rule set twice. The tool gate runs AFTER the parse and only
    ///     if it succeeded: the structural rules throw first, and a graph nobody can walk has no tools worth naming.
    /// </remarks>
    private async Task<GraphWorkflowGraph> ValidateAndParseAsync(string graphJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
        {
            throw new GraphWorkflowValidationException("A graph workflow definition needs a graph.");
        }

        var graph = GraphWorkflowGraphContract.ValidateAndParse(graphJson, _options.Value.MaxNodesPerDefinition);
        var toolErrors = await GraphWorkflowToolGate.ErrorsAsync(graph, _tools, cancellationToken);
        return toolErrors.Count == 0
            ? graph
            : throw new GraphWorkflowValidationException(GraphWorkflowValidationResult.Invalid(toolErrors));
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
            if (warning.Key is not { } nodeKey
                || !graph.Nodes.TryGetValue(nodeKey, out var node)
                || node.Config is not GraphWorkflowAgentConfig { Model: { } model }
                || string.IsNullOrWhiteSpace(model))
            {
                continue;
            }

            if (await ServedByLlamaServerAsync(model, cancellationToken))
            {
                suppressed.Add(warning);
            }
        }

        return suppressed.Count == 0 ? graph.Warnings : [.. graph.Warnings.Where(warning => !suppressed.Contains(warning))];
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
