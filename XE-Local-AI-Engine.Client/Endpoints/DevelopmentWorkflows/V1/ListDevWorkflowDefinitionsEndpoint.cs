namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>The definition picker's feed. Never loads a graph blob: the node count is a column, not a parse.</summary>
public sealed class ListDevWorkflowDefinitionsEndpoint : Endpoint<ListDevWorkflowDefinitionsRequest, ListDevWorkflowDefinitionsResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public ListDevWorkflowDefinitionsEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListDevWorkflowDefinitionsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var definitions = await _authoring.ListDefinitionsAsync(req.IncludeArchived, ct);
        await Send.OkAsync(new ListDevWorkflowDefinitionsResponse { Items = [.. definitions.Select(DevWorkflowContractMapper.ToResponse)] }, ct);
    }
}
