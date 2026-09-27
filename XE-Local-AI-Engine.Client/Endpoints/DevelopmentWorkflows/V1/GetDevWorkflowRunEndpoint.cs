namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class GetDevWorkflowRunEndpoint : Endpoint<DevWorkflowRunRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunQueryService _queries;
    private readonly IDevWorkflowRunService _runs;

    public GetDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunQueryService queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(runs);
        _queries = queries;
        _runs = runs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.GetAsync(req.RunId, ct);
        await Send.OkAsync((await _queries.GetRunViewAsync(detail, ct)).ToResponse(), ct);
    }
}
