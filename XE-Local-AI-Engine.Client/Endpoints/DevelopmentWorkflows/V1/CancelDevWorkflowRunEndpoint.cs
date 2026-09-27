namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class CancelDevWorkflowRunEndpoint : Endpoint<DevWorkflowRunActionRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunQueryService _queries;
    private readonly IDevWorkflowRunService _runs;

    public CancelDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunQueryService queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(runs);
        _queries = queries;
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<DevWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(DevWorkflowRunActionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.CancelAsync(req.RunId, req.OperationId, ct);
        await Send.ResultAsync(Results.Accepted(value: (await _queries.GetRunViewAsync(detail, ct)).ToResponse()));
    }
}
