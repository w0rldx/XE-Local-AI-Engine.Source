namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The one write seam for graph workflow definitions: every save comes through here, so the parse and the node cap
///     have a single home and no caller — the API, or S4's importer — can store a graph the dispatcher could not route.
///     <para>
///         Three members and no more. Reads (list, get, delete) go straight to <see cref="IGraphWorkflowStore" />: they
///         have no validation to own, and a pass-through here would be a second name for the same call.
///     </para>
/// </summary>
public interface IGraphWorkflowDefinitionService
{
    /// <summary>
    ///     Every complaint about <paramref name="graphJson" />, keyed by the node or edge it belongs to. NEVER throws:
    ///     the editor asks this question while a graph is still half-written, and a caller that has to catch to read
    ///     an answer cannot append its own failures to the list — which is exactly what the tool gate does later.
    /// </summary>
    GraphWorkflowValidationResult Validate(string graphJson);

    /// <summary>Validates and stores. Throws <see cref="GraphWorkflowValidationException" /> before reaching the store.</summary>
    Task<GraphWorkflowDefinitionSnapshot> CreateAsync(string name, string? description, string graphJson, CancellationToken cancellationToken = default);

    /// <summary>
    ///     A partial edit under optimistic concurrency: every null member leaves the stored value alone, so a rename
    ///     travels without the caller echoing back a graph it never read. A non-null graph is validated and its node
    ///     count written alongside it.
    /// </summary>
    Task<GraphWorkflowDefinitionSnapshot> UpdateAsync(Guid definitionId,
        int expectedVersion,
        string? name,
        string? description,
        string? graphJson,
        CancellationToken cancellationToken = default);
}
