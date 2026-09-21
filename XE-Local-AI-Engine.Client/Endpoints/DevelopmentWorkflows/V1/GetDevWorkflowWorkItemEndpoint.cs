namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class GetDevWorkflowWorkItemEndpoint : Endpoint<DevWorkflowWorkItemRequest, DevWorkflowWorkItemResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    private readonly DevWorkflowRunQueryService _runQueries;

    public GetDevWorkflowWorkItemEndpoint(DevWorkflowAuthoringService authoring, DevWorkflowRunQueryService runQueries)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        ArgumentNullException.ThrowIfNull(runQueries);
        _authoring = authoring;
        _runQueries = runQueries;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.WorkItemById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowWorkItemRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var workItem = await _authoring.GetWorkItemAsync(req.WorkItemId, ct);

        // The detail embeds its runs rather than making the client follow a link: a work item's history is short, and
        // the run list is the first thing the detail page draws.
        var runs = await _runQueries.ListRunSummariesAsync(req.WorkItemId, cancellationToken: ct);
        await Send.OkAsync(workItem.ToResponse(runs), ct);
    }
}
