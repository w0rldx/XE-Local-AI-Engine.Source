namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The Development-Workflow AUTHORING endpoints' only door onto <see cref="IDevWorkflowStore" />: the definitions,
///     rule sets and work items a human creates, edits and retires before any run exists.
/// </summary>
/// <remarks>
///     An endpoint is the HTTP edge and may not take a persistence store itself, so each call arrives here unchanged.
///     This type adds no ordering or defaulting, and no validation beyond a work-item create's project check: graph
///     validation, the request-size refusal and the status-filter parse stay in the endpoints, and the store keeps
///     deciding not-found, version conflicts and what a null field means. Everything that MOVES a run is deliberately elsewhere — commands on
///     <see cref="IDevWorkflowRunService" />, run-feed reads on <see cref="DevWorkflowRunQueryService" />.
/// </remarks>
public sealed class DevWorkflowAuthoringService
{
    private readonly IDevelopmentStore? _development;
    private readonly IDevWorkflowStore _store;

    // The development store is registered only while Development Mode is on; null means no project can be bound.
    public DevWorkflowAuthoringService(IDevWorkflowStore store, IDevelopmentStore? development = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _development = development;
    }

    /// <summary>The definition picker's feed. Never loads a graph blob: the node count is a column, not a parse.</summary>
    public Task<IReadOnlyList<DevWorkflowDefinitionSummary>> ListDefinitionsAsync(bool includeArchived = false,
        CancellationToken cancellationToken = default)
    {
        return _store.ListDefinitionsAsync(includeArchived, cancellationToken);
    }

    /// <summary>Stores a new definition with the graph and node count the caller already validated.</summary>
    public Task<DevWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateDevWorkflowDefinitionCommand command,
        CancellationToken cancellationToken = default)
    {
        return _store.CreateDefinitionAsync(command, cancellationToken);
    }

    /// <summary>One definition in full, graph included.</summary>
    public Task<DevWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        return _store.GetDefinitionAsync(definitionId, cancellationToken);
    }

    /// <summary>Applies an edit against the version it was made from; a null graph leaves the stored one alone.</summary>
    public Task<DevWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateDevWorkflowDefinitionCommand command,
        CancellationToken cancellationToken = default)
    {
        return _store.UpdateDefinitionAsync(command, cancellationToken);
    }

    /// <summary>Archives rather than deletes: runs that pinned the definition keep rendering.</summary>
    public Task<DevWorkflowDefinitionSnapshot> ArchiveDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        return _store.ArchiveDefinitionAsync(definitionId, cancellationToken);
    }

    /// <summary>The rule-set list. Never loads a body: it is the encrypted column, and the list has no use for it.</summary>
    public Task<IReadOnlyList<DevWorkflowRuleSetSummary>> ListRuleSetsAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListRuleSetsAsync(cancellationToken);
    }

    /// <summary>Stores a new rule set, body and scope included.</summary>
    public Task<DevWorkflowRuleSetSnapshot> CreateRuleSetAsync(CreateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default)
    {
        return _store.CreateRuleSetAsync(command, cancellationToken);
    }

    /// <summary>One rule set in full, body included.</summary>
    public Task<DevWorkflowRuleSetSnapshot> GetRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default)
    {
        return _store.GetRuleSetAsync(ruleSetId, cancellationToken);
    }

    /// <summary>Replaces the whole document, refusing an edit made against a version that has since moved on.</summary>
    public Task<DevWorkflowRuleSetSnapshot> UpdateRuleSetAsync(UpdateDevWorkflowRuleSetCommand command, CancellationToken cancellationToken = default)
    {
        return _store.UpdateRuleSetAsync(command, cancellationToken);
    }

    /// <summary>A HARD delete: each node run recorded the text that applied to it, so the audit outlives the document.</summary>
    public Task DeleteRuleSetAsync(Guid ruleSetId, CancellationToken cancellationToken = default)
    {
        return _store.DeleteRuleSetAsync(ruleSetId, cancellationToken);
    }

    /// <summary>The work-item page, each row carrying its latest run's status and node counters.</summary>
    public Task<IReadOnlyList<DevWorkflowWorkItemSnapshot>> ListWorkItemsAsync(DevWorkflowWorkItemStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        return _store.ListWorkItemsAsync(status, cancellationToken);
    }

    /// <summary>Creates a work item. Definition-agnostic: the definition is chosen per RUN.</summary>
    /// <remarks>
    ///     The one check this type makes: a named development project has to exist. The column carries no foreign key,
    ///     so an unknown id would be stored and only fail at the first run's first repository command.
    /// </remarks>
    public async Task<DevWorkflowWorkItemSnapshot> CreateWorkItemAsync(CreateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.DevelopmentProjectId is { } projectId)
        {
            if (_development is null)
            {
                throw new DevWorkflowValidationException("Development Mode is off on this node, so the work item cannot be bound to a development project. Leave the project out.");
            }

            try
            {
                _ = await _development.GetProjectAsync(projectId, cancellationToken);
            }
            catch (DevelopmentNotFoundException)
            {
                throw new DevWorkflowValidationException($"There is no development project '{projectId}', so the work item cannot be bound to it. "
                                                         + "Pick an existing project, or leave the project out.");
            }
        }

        return await _store.CreateWorkItemAsync(command, cancellationToken);
    }

    /// <summary>One work item, without its runs — the caller embeds those from <see cref="DevWorkflowRunQueryService" />.</summary>
    public Task<DevWorkflowWorkItemSnapshot> GetWorkItemAsync(Guid workItemId, CancellationToken cancellationToken = default)
    {
        return _store.GetWorkItemAsync(workItemId, cancellationToken);
    }

    /// <summary>Patches title and request; a null member means "leave it alone", which is the store's reading, not this one's.</summary>
    public Task<DevWorkflowWorkItemSnapshot> UpdateWorkItemAsync(UpdateDevWorkflowWorkItemCommand command, CancellationToken cancellationToken = default)
    {
        return _store.UpdateWorkItemAsync(command, cancellationToken);
    }
}
