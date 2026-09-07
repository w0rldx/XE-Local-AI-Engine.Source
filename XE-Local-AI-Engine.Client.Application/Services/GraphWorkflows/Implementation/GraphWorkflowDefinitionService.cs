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
internal sealed class GraphWorkflowDefinitionService(IGraphWorkflowStore store,
    IToolInvocationService tools,
    ILocalModelProviderResolver providers,
    IOptions<GraphWorkflowOptions> options)
    : IGraphWorkflowDefinitionService
{
    private readonly IGraphWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly IToolInvocationService _tools = tools ?? throw new ArgumentNullException(nameof(tools));

    private readonly ILocalModelProviderResolver _providers = providers ?? throw new ArgumentNullException(nameof(providers));

    private readonly IOptions<GraphWorkflowOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<GraphWorkflowValidationResult> ValidateAsync(string graphJson, CancellationToken cancellationToken = default)
    {
        try
        {
            // A graph that routes may still have something said about it. Warnings ride out only on this path: they
            // never block, so the save and start paths below discard them rather than pretend to act on them.
            var graph = await ValidateAndParseAsync(graphJson, cancellationToken).ConfigureAwait(false);
            return GraphWorkflowValidationResult.ValidWith(await WarningsForRuntimeAsync(graph, cancellationToken).ConfigureAwait(false));
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
        var nodeCount = (await ValidateAndParseAsync(graphJson, cancellationToken).ConfigureAwait(false)).Nodes.Count;
        return await _store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(Guid.NewGuid(), name, graphJson, nodeCount, Description: description),
                               cancellationToken)
                           .ConfigureAwait(false);
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
        int? nodeCount = graphJson is null ? null : (await ValidateAndParseAsync(graphJson, cancellationToken).ConfigureAwait(false)).Nodes.Count;
        return await _store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(definitionId, expectedVersion, name, description, graphJson, nodeCount),
                               cancellationToken)
                           .ConfigureAwait(false);
    }

    /// <summary>
    ///     The one place the option-bearing half of validation lives. A blank document is turned into the same
    ///     structured refusal every other whole-document failure produces, so <see cref="ValidateAsync" /> can promise
    ///     never to throw rather than leaking the parser's argument guard. Answers the PARSED graph rather than a node
    ///     count, because two callers want the count and one wants the warnings, and re-parsing for either would run
    ///     the rule set twice.
    ///     <para>
    ///         The tool gate runs AFTER the parse and only if it succeeded: the structural rules throw first, and there
    ///         is nothing useful to say about the tools of a graph nobody can walk.
    ///     </para>
    /// </summary>
    private async Task<GraphWorkflowGraph> ValidateAndParseAsync(string graphJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(graphJson))
        {
            throw new GraphWorkflowValidationException("A graph workflow definition needs a graph.");
        }

        var graph = GraphWorkflowGraphContract.ValidateAndParse(graphJson, _options.Value.MaxNodesPerDefinition);
        var toolErrors = await GraphWorkflowToolGate.ErrorsAsync(graph, _tools, cancellationToken).ConfigureAwait(false);
        return toolErrors.Count == 0
            ? graph
            : throw new GraphWorkflowValidationException(GraphWorkflowValidationResult.Invalid(toolErrors));
    }

    /// <summary>
    ///     The graph's warnings, minus the response-schema one on any Agent node whose pinned model llama-server
    ///     serves. That warning describes the <c>Microsoft.Extensions.AI.OpenAI</c> strict-schema rewrite, and the
    ///     llama.cpp lane no longer suffers it: the schema is written onto the request body as authored
    ///     (<c>DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough</c>). Telling an author their bounds are
    ///     dropped when the runtime enforces them is worse than saying nothing.
    ///     <para>
    ///         Here rather than in the parser because only this seam can reach the model-to-provider map, and only this
    ///         path answers warnings at all. A node with no model pin, or one that maps to any other provider, keeps
    ///         its warning: what an unpinned node inherits is decided at run start, and being wrong in the direction of
    ///         a warning nobody needed is the cheap error.
    ///     </para>
    /// </summary>
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

            if (await ServedByLlamaServerAsync(model, cancellationToken).ConfigureAwait(false))
            {
                suppressed.Add(warning);
            }
        }

        return suppressed.Count == 0 ? graph.Warnings : [.. graph.Warnings.Where(warning => !suppressed.Contains(warning))];
    }

    /// <summary>
    ///     Whether llama-server serves <paramref name="model" />, answering <see langword="false" /> when the lookup
    ///     cannot say. The resolver opens a scope, takes a map read lease and reads the store, so it can fail for
    ///     reasons that have nothing to do with the graph being validated — and validation is a warning-only,
    ///     never-blocking path that until now touched only the parser and the tool catalog. Letting a store fault
    ///     escape would turn an editor's probe into a 500. Answering <see langword="false" /> keeps the warning, which
    ///     is the same cheap-error direction an unpinned node already takes.
    /// </summary>
    private async Task<bool> ServedByLlamaServerAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            var provider = await _providers.ResolveProviderNameForModelAsync(model, cancellationToken).ConfigureAwait(false);
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
