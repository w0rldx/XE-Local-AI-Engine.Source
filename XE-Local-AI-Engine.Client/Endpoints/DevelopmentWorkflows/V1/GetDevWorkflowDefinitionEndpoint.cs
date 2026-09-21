namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class GetDevWorkflowDefinitionEndpoint : Endpoint<DevWorkflowDefinitionRequest, DevWorkflowDefinitionResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public GetDevWorkflowDefinitionEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var definition = await _authoring.GetDefinitionAsync(req.DefinitionId, ct);
        await Send.OkAsync(definition.ToResponse(), ct);
    }
}
