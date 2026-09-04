namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>The definition picker's feed. Never loads a graph blob: the node count is a column, not a parse.</summary>
public sealed class ListDevWorkflowDefinitionsEndpoint(IDevWorkflowStore store)
    : Endpoint<ListDevWorkflowDefinitionsRequest, ListDevWorkflowDefinitionsResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListDevWorkflowDefinitionsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var definitions = await _store.ListDefinitionsAsync(req.IncludeArchived, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevWorkflowDefinitionsResponse([.. definitions.Select(DevWorkflowContractMapper.ToResponse)]), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Creates a definition, refusing a graph the dispatcher could not route.
///     <para>
///         Validated by the RUNTIME's parser rather than by a validator of this endpoint's own: it is the same parser
///         run start uses, so a graph accepted here is one that will start, and a rule added there cannot be forgotten
///         here. Its refusal is a single message, and the global validation handler shapes it into the same 400 body
///         every other domain refusal produces.
///     </para>
/// </summary>
public sealed class CreateDevWorkflowDefinitionEndpoint(IDevWorkflowStore store) : Endpoint<CreateDevWorkflowDefinitionRequest, DevWorkflowDefinitionResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
        // 201 is what the success path actually sends, so it is declared: the generated client narrows the create
        // response off this, and a route documented as 400-only would type no success body at all.
        Description(static builder => builder.Produces<DevWorkflowDefinitionResponse>(StatusCodes.Status201Created)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(CreateDevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var graphJson = DevWorkflowContractMapper.ToGraphJson(req.Graph);
        var nodeCount = DevWorkflowGraphContract.ValidateAndCountNodes(graphJson);
        var created = await _store.CreateDefinitionAsync(new CreateDevWorkflowDefinitionCommand(Guid.NewGuid(), req.Name, graphJson, nodeCount), ct)
                                  .ConfigureAwait(false);
        await Send.CreatedAtAsync<GetDevWorkflowDefinitionEndpoint>(new
            {
                definitionId = created.Id
            },
            created.ToResponse(),
            cancellation: ct).ConfigureAwait(false);
    }
}

public sealed class GetDevWorkflowDefinitionEndpoint(IDevWorkflowStore store) : Endpoint<DevWorkflowDefinitionRequest, DevWorkflowDefinitionResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var definition = await _store.GetDefinitionAsync(req.DefinitionId, ct).ConfigureAwait(false);
        await Send.OkAsync(definition.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class UpdateDevWorkflowDefinitionEndpoint(IDevWorkflowStore store) : Endpoint<UpdateDevWorkflowDefinitionRequest, DevWorkflowDefinitionResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Put(LocalApiRoutes.DevelopmentWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateDevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // A null graph leaves the stored one alone — a rename must not have to echo a graph back to keep it. Runs that
        // already pinned this definition are unaffected either way: they carry their own snapshot.
        string? graphJson = null;
        int? nodeCount = null;
        if (req.Graph is { } graph)
        {
            graphJson = DevWorkflowContractMapper.ToGraphJson(graph);
            nodeCount = DevWorkflowGraphContract.ValidateAndCountNodes(graphJson);
        }

        var updated = await _store.UpdateDefinitionAsync(new UpdateDevWorkflowDefinitionCommand(req.DefinitionId, req.Version, req.Name, graphJson, nodeCount), ct)
                                  .ConfigureAwait(false);
        await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Archives a definition rather than deleting it: every run that pinned it keeps rendering, and a definition
///     cannot become permanently undeletable because a year-old run still references it. It disappears from the
///     picker and from the default list.
/// </summary>
public sealed class ArchiveDevWorkflowDefinitionEndpoint(IDevWorkflowStore store) : Endpoint<DevWorkflowDefinitionRequest>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Delete(LocalApiRoutes.DevelopmentWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        _ = await _store.ArchiveDefinitionAsync(req.DefinitionId, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
