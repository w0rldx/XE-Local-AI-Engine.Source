namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The one write seam for graph workflow definitions: every save comes through here, so the parse and the node cap
///     have a single home and no caller — the API, or the canvas importer — can store a graph nothing could route.
/// </summary>
/// <remarks>
///     The reads (<see cref="ListAsync" />, <see cref="GetAsync" />, <see cref="DeleteAsync" />) are here because the
///     endpoint-dependency rule (<c>EndpointDependencyTests</c>, <c>docs/wiki/16-code-conventions.md</c>) makes this
///     service the endpoints' only door to <see cref="IGraphWorkflowStore" />. They are deliberate pass-throughs with
///     no validation of their own: what the store promises about the graph blob and what it refuses while a run is
///     live are answered there and restated nowhere.
/// </remarks>
public interface IGraphWorkflowDefinitionService
{
    /// <summary>Every complaint about <paramref name="graphJson" />, keyed by the node or edge it belongs to.</summary>
    /// <remarks>
    ///     NEVER throws: the editor asks this while a graph is still half-written, and a caller that has to catch to
    ///     read an answer cannot append its own failures to the list. Asynchronous because the tool gate is — whether
    ///     a <c>Tool</c> node's name is invocable is a question for the live tool catalog, and asking it here is what
    ///     keeps the editor's report and the runtime's refusal the same answer.
    /// </remarks>
    Task<GraphWorkflowValidationResult> ValidateAsync(string graphJson, CancellationToken cancellationToken = default);

    /// <summary>Validates and stores. Throws <see cref="GraphWorkflowValidationException" /> before reaching the store.</summary>
    Task<GraphWorkflowDefinitionSnapshot> CreateAsync(string name, string? description, string graphJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A partial edit under optimistic concurrency: every null member leaves the stored value alone, so a rename
    ///     travels without the caller echoing back a graph it never read.
    /// </summary>
    /// <remarks>
    ///     A non-null graph is validated and its node count written alongside it. <paramref name="description" />
    ///     distinguishes the two absences: null leaves the stored description alone, an EMPTY string clears it.
    ///     Collapsing them would mean an author who deleted the text had no way to say so, or a rename that carried
    ///     no description silently wiped one.
    /// </remarks>
    Task<GraphWorkflowDefinitionSnapshot> UpdateAsync(Guid definitionId,
        int expectedVersion,
        string? name,
        string? description,
        string? graphJson,
        CancellationToken cancellationToken = default);

    /// <summary>The picker's feed, exactly as <see cref="IGraphWorkflowStore.ListDefinitionsAsync" /> answers it.</summary>
    Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One definition in full, exactly as <see cref="IGraphWorkflowStore.GetDefinitionAsync" /> answers it.</summary>
    Task<GraphWorkflowDefinitionSnapshot> GetAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>Deletes, on <see cref="IGraphWorkflowStore.DeleteDefinitionAsync" />'s terms — the live-run refusal included.</summary>
    Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default);
}
