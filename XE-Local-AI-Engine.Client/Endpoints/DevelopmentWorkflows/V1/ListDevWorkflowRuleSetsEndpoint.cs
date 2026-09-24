namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>The rule-set list. Never loads a body: it is the encrypted column, and the list has no use for it.</summary>
public sealed class ListDevWorkflowRuleSetsEndpoint : EndpointWithoutRequest<ListDevWorkflowRuleSetsResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public ListDevWorkflowRuleSetsEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RuleSets);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var ruleSets = await _authoring.ListRuleSetsAsync(ct);
        await Send.OkAsync(new ListDevWorkflowRuleSetsResponse
        {
            Items = [.. ruleSets.Select(DevWorkflowContractMapper.ToResponse)]
        }, ct);
    }
}
