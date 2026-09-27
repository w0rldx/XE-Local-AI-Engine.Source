namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     One node run in full. The agent view is reached from here through <c>workSessionId</c> and the EXISTING
///     work-session routes rather than through observability endpoints of this surface's own — two ways to read one
///     session's events would drift.
/// </summary>
public sealed class GetDevWorkflowNodeRunEndpoint : Endpoint<DevWorkflowNodeRunRequest, DevWorkflowNodeRunDetailResponse>
{
    private readonly DevWorkflowRunQueryService _queries;

    public GetDevWorkflowNodeRunEndpoint(DevWorkflowRunQueryService queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        _queries = queries;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.NodeRunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowNodeRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await Send.OkAsync((await _queries.GetNodeRunViewAsync(req.RunId, req.NodeRunId, ct)).ToResponse(), ct);
    }
}
