namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Removes a work item and everything under it.
/// </summary>
/// <remarks>
///     Delegated to the runtime rather than written here, because the rows are the smaller half: the work sessions the
///     agent node runs own and the artifact bytes on disk go with them, and neither is something the store can reach.
/// </remarks>
public sealed class DeleteDevWorkflowWorkItemEndpoint : Endpoint<DevWorkflowWorkItemRequest>
{
    private readonly IDevWorkflowRunService _runs;

    public DeleteDevWorkflowWorkItemEndpoint(IDevWorkflowRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

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

        await _runs.DeleteWorkItemAsync(req.WorkItemId, ct);
        await Send.NoContentAsync(ct);
    }
}
