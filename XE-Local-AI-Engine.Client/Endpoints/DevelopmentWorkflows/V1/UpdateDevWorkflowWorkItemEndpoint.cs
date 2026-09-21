namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class UpdateDevWorkflowWorkItemEndpoint : Endpoint<UpdateDevWorkflowWorkItemRequest, DevWorkflowWorkItemResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    private readonly DevWorkflowRunQueryService _runQueries;

    public UpdateDevWorkflowWorkItemEndpoint(DevWorkflowAuthoringService authoring, DevWorkflowRunQueryService runQueries)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(runQueries);
        _authoring = authoring;
        _runQueries = runQueries;
    }

    public override void Configure()
    {
        Patch(LocalApiRoutes.DevelopmentWorkflows.WorkItemById);
        Policies(NodeAuthorizationPolicies.Operator);

        // No 409 declared: this PATCH writes against the Any version sentinel, so it has no version race to lose — the only other writer to a work item is the runtime
        // writing its STATUS, which this never touches. Declaring one would put a response in the generated client that the endpoint cannot send.
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(UpdateDevWorkflowWorkItemRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // An omitted member is forwarded as null, which the store reads as "leave it alone" — a PATCH that only renames must not blank the request it never mentioned.
        // There is no expected version: the only other writer to a work item is the runtime writing its STATUS, which this cannot collide with.
        var updated = await _authoring.UpdateWorkItemAsync(new UpdateDevWorkflowWorkItemCommand { WorkItemId = req.WorkItemId, ExpectedVersion = DevWorkflowVersions.Any, Title = req.Title, Request = req.Request }, ct);
        var runs = await _runQueries.ListRunSummariesAsync(req.WorkItemId, cancellationToken: ct);
        await Send.OkAsync(updated.ToResponse(runs), ct);
    }
}
