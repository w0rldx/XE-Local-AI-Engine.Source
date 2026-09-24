namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class ListDevWorkflowRunsEndpoint : Endpoint<ListDevWorkflowRunsRequest, ListDevWorkflowRunsResponse>
{
    private readonly DevWorkflowRunQueryService _runQueries;

    public ListDevWorkflowRunsEndpoint(DevWorkflowRunQueryService runQueries)
    {
        ArgumentNullException.ThrowIfNull(runQueries);
        _runQueries = runQueries;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ListDevWorkflowRunsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not a member.
        var status = req.Status is null ? (DevWorkflowRunStatus?)null : Enum.Parse<DevWorkflowRunStatus>(req.Status, ignoreCase: true);
        var runs = await _runQueries.ListRunSummariesAsync(req.WorkItemId, status, req.Limit, ct);
        await Send.OkAsync(new ListDevWorkflowRunsResponse
        {
            Items = [.. runs.Select(DevWorkflowContractMapper.ToResponse)]
        }, ct);
    }
}
