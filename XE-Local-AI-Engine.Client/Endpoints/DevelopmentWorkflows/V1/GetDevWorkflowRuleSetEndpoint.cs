namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class GetDevWorkflowRuleSetEndpoint : Endpoint<DevWorkflowRuleSetRequest, DevWorkflowRuleSetResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public GetDevWorkflowRuleSetEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RuleSetById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowRuleSetRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var ruleSet = await _authoring.GetRuleSetAsync(req.RuleSetId, ct);
        await Send.OkAsync(ruleSet.ToResponse(), ct);
    }
}
