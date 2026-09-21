namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

public sealed class CreateDevWorkflowRuleSetEndpoint : Endpoint<CreateDevWorkflowRuleSetRequest, DevWorkflowRuleSetResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public CreateDevWorkflowRuleSetEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.RuleSets);
        Policies(NodeAuthorizationPolicies.Operator);
        // 201 is what the success path actually sends, so it is declared: the generated client narrows the create
        // response off this, and a route documented as 400-only would type no success body at all.
        Description(static builder => builder.Produces<DevWorkflowRuleSetResponse>(StatusCodes.Status201Created)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(CreateDevWorkflowRuleSetRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var created = await _authoring.CreateRuleSetAsync(new CreateDevWorkflowRuleSetCommand
        {
            RuleSetId = Guid.NewGuid(),
            Name = req.Name,
            Body = req.Body,
            ScopeJson = DevWorkflowContractMapper.ToScopeJson(req.Scope),
            Description = req.Description,
            Enabled = req.Enabled
        },
                                      ct);
        await Send.CreatedAtAsync<GetDevWorkflowRuleSetEndpoint>(new
            {
                ruleSetId = created.Id
            },
            created.ToResponse(),
            cancellation: ct);
    }
}
