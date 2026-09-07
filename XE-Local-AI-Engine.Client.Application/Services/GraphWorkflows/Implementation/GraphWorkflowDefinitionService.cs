namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     Validation and the store, in that order. The parse it runs is the RUNTIME's own, so a definition accepted here
///     is one that will start, and a rule added to the parser cannot be forgotten on the save path.
/// </summary>
internal sealed class GraphWorkflowDefinitionService(IGraphWorkflowStore store, IToolInvocationService tools, IOptions<GraphWorkflowOptions> options)
    : IGraphWorkflowDefinitionService
{
    private readonly IGraphWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly IToolInvocationService _tools = tools ?? throw new ArgumentNullException(nameof(tools));

    private readonly IOptions<GraphWorkflowOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<GraphWorkflowValidationResult> ValidateAsync(string graphJson, CancellationToken cancellationToken = default)
    {
        try
        {
            // A graph that routes may still have something said about it. Warnings ride out only on this path: they
            // never block, so the save and start paths below discard them rather than pretend to act on them.
            return GraphWorkflowValidationResult.ValidWith((await ValidateAndParseAsync(graphJson, cancellationToken).ConfigureAwait(false)).Warnings);
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
}
