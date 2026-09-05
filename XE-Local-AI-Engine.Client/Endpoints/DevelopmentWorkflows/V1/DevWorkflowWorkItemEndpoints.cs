namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     The work-item list. Each row carries its latest run's status and node counters, so the page renders without a
///     per-row fetch — which is what makes polling it honest rather than a fan-out.
/// </summary>
public sealed class ListDevWorkflowWorkItemsEndpoint(IDevWorkflowStore store) : Endpoint<ListDevWorkflowWorkItemsRequest, ListDevWorkflowWorkItemsResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.WorkItems);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ListDevWorkflowWorkItemsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not a member.
        var status = req.Status is null ? (DevWorkflowWorkItemStatus?)null : Enum.Parse<DevWorkflowWorkItemStatus>(req.Status, ignoreCase: true);
        var items = await _store.ListWorkItemsAsync(status, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevWorkflowWorkItemsResponse([.. items.Select(DevWorkflowContractMapper.ToSummaryResponse)]), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Creates a work item. Deliberately definition-agnostic: the definition is chosen per RUN, which is what lets one
///     work item be re-run against a revised definition later.
/// </summary>
public sealed class CreateDevWorkflowWorkItemEndpoint(IDevWorkflowStore store) : Endpoint<CreateDevWorkflowWorkItemRequest, DevWorkflowWorkItemResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.WorkItems);
        Policies(NodeAuthorizationPolicies.Operator);
        // 201 is what the success path actually sends, so it is declared: the generated client narrows the create
        // response off this, and a route documented as 400-only would type no success body at all.
        Description(static builder => builder.Produces<DevWorkflowWorkItemResponse>(StatusCodes.Status201Created)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(CreateDevWorkflowWorkItemRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var created = await _store.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand(Guid.NewGuid(), req.Title, req.Request, req.DevelopmentProjectId), ct)
                                  .ConfigureAwait(false);
        await Send.CreatedAtAsync<GetDevWorkflowWorkItemEndpoint>(new
            {
                workItemId = created.Id
            },
            created.ToResponse([]),
            cancellation: ct).ConfigureAwait(false);
    }
}

public sealed class GetDevWorkflowWorkItemEndpoint(IDevWorkflowStore store) : Endpoint<DevWorkflowWorkItemRequest, DevWorkflowWorkItemResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.WorkItemById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowWorkItemRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var workItem = await _store.GetWorkItemAsync(req.WorkItemId, ct).ConfigureAwait(false);

        // The detail embeds its runs rather than making the client follow a link: a work item's history is short, and
        // the run list is the first thing the detail page draws.
        var runs = await _store.ListRunSummariesAsync(req.WorkItemId, cancellationToken: ct).ConfigureAwait(false);
        await Send.OkAsync(workItem.ToResponse(runs), ct).ConfigureAwait(false);
    }
}

public sealed class UpdateDevWorkflowWorkItemEndpoint(IDevWorkflowStore store) : Endpoint<UpdateDevWorkflowWorkItemRequest, DevWorkflowWorkItemResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Patch(LocalApiRoutes.DevelopmentWorkflows.WorkItemById);
        Policies(NodeAuthorizationPolicies.Operator);

        // No 409 declared: this PATCH writes against the Any version sentinel, so it has no version race to lose —
        // the only other writer to a work item is the runtime writing its STATUS, which this never touches. Declaring
        // one would put a response in the generated client that the endpoint cannot send.
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(UpdateDevWorkflowWorkItemRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // An omitted member is forwarded as null, which the store reads as "leave it alone" — a PATCH that only
        // renames must not blank the request it never mentioned. There is no expected version: the only other writer
        // to a work item is the runtime writing its STATUS, which this cannot collide with.
        var updated = await _store.UpdateWorkItemAsync(new UpdateDevWorkflowWorkItemCommand(req.WorkItemId, DevWorkflowVersions.Any, req.Title, req.Request), ct)
                                  .ConfigureAwait(false);
        var runs = await _store.ListRunSummariesAsync(req.WorkItemId, cancellationToken: ct).ConfigureAwait(false);
        await Send.OkAsync(updated.ToResponse(runs), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Removes a work item and everything under it. Delegated to the runtime rather than written here, because the
///     rows are the smaller half: the work sessions the agent node runs own and the artifact bytes on disk go with
///     them, and neither is something the store can reach.
/// </summary>
public sealed class DeleteDevWorkflowWorkItemEndpoint(IDevWorkflowRunService runs) : Endpoint<DevWorkflowWorkItemRequest>
{
    private readonly IDevWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Delete(LocalApiRoutes.DevelopmentWorkflows.WorkItemById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DevWorkflowWorkItemRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await _runs.DeleteWorkItemAsync(req.WorkItemId, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
