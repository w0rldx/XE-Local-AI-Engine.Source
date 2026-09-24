namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Replaces the whole document, refusing an edit made against a version that has since moved on.
/// </summary>
/// <remarks>
///     Editing a rule set while a run is in flight is allowed, and deliberately: each node run recorded the
///     <c>{id, name, contentSha256}</c> that applied to it, so the audit keeps naming the exact text it was given and
///     the hash is what says the current document is no longer that text.
/// </remarks>
public sealed class UpdateDevWorkflowRuleSetEndpoint : Endpoint<UpdateDevWorkflowRuleSetRequest, DevWorkflowRuleSetResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public UpdateDevWorkflowRuleSetEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.DevelopmentWorkflows.RuleSetById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateDevWorkflowRuleSetRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var updated = await _authoring.UpdateRuleSetAsync(new UpdateDevWorkflowRuleSetCommand
            {
                RuleSetId = req.RuleSetId,
                ExpectedVersion = req.Version,
                Name = req.Name,
                Body = req.Body,
                ScopeJson = DevWorkflowContractMapper.ToScopeJson(req.Scope),
                Description = req.Description,
                Enabled = req.Enabled
            },
            ct);
        await Send.OkAsync(updated.ToResponse(), ct);
    }
}
