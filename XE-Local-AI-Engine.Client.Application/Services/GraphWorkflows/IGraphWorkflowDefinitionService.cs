namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Tools;

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

    /// <summary>The same report, with the tool gate judging action tools under <paramref name="allowsActions" /> rather than admitting them.</summary>
    /// <remarks>For a caller that knows which definition the graph belongs to, as the workflow designer does.</remarks>
    Task<GraphWorkflowValidationResult> ValidateAsync(string graphJson, bool allowsActions, CancellationToken cancellationToken = default);

    /// <summary>Validates and stores. Throws <see cref="GraphWorkflowValidationException" /> before reaching the store.</summary>
    Task<GraphWorkflowDefinitionSnapshot> CreateAsync(string name, string? description, string graphJson, CancellationToken cancellationToken = default);

    /// <summary>Create for a share file: every rule but the action flag, which an import never sets (ADR 0021).</summary>
    /// <remarks>
    ///     The definition is stored with <c>AllowsActions</c> false, so an action node arrives unable to act: the start
    ///     refuses it until the operator flags the definition through the update. Refusing the import instead would leave
    ///     no definition to flag.
    /// </remarks>
    Task<GraphWorkflowDefinitionSnapshot> ImportAsync(string name, string? description, string graphJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A partial edit under optimistic concurrency: every null member leaves the stored value alone, so a rename
    ///     travels without the caller echoing back a graph it never read.
    /// </summary>
    /// <remarks>
    ///     A non-null graph is validated and its node count written alongside it. <paramref name="description" />
    ///     distinguishes the two absences: null leaves the stored description alone, an EMPTY string clears it.
    ///     Collapsing them would mean an author who deleted the text had no way to say so, or a rename that carried
    ///     no description silently wiped one. A graph is gated under <paramref name="allowsActions" />, or the stored flag.
    ///     <paramref name="triggerSources" /> is an operator setting like the flag; null leaves it alone.
    /// </remarks>
    Task<GraphWorkflowDefinitionSnapshot> UpdateAsync(Guid definitionId,
        int expectedVersion,
        string? name,
        string? description,
        string? graphJson,
        bool? allowsActions = null,
        GraphWorkflowTriggerSource? triggerSources = null,
        CancellationToken cancellationToken = default);

    /// <summary>The picker's feed, exactly as <see cref="IGraphWorkflowStore.ListDefinitionsAsync" /> answers it.</summary>
    Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One definition in full, exactly as <see cref="IGraphWorkflowStore.GetDefinitionAsync" /> answers it.</summary>
    Task<GraphWorkflowDefinitionSnapshot> GetAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>Deletes, on <see cref="IGraphWorkflowStore.DeleteDefinitionAsync" />'s terms — the live-run refusal included.</summary>
    Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     What a <c>Tool</c> node may name: the invocation envelope, plus <c>web_fetch</c> flagged
    ///     <see cref="InvocableToolDescriptor.RequiresAllowedUrls" /> while web access is on. The same list the save gate checks.
    /// </summary>
    Task<IReadOnlyList<InvocableToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken = default);
}
