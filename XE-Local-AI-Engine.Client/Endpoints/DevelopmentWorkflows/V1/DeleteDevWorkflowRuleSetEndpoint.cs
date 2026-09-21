namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     A HARD delete, unlike a definition's archive, and it does not refuse while a run is in flight.
/// </summary>
/// <remarks>
///     Nothing holds a foreign key to a rule set, and what a node run needs from one — which document applied, at
///     which text — it copied onto its own row at materialization. The objective composer skips a document that is
///     gone.
/// </remarks>
public sealed class DeleteDevWorkflowRuleSetEndpoint : Endpoint<DevWorkflowRuleSetRequest>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public DeleteDevWorkflowRuleSetEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.DevelopmentWorkflows.RuleSetById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowRuleSetRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await _authoring.DeleteRuleSetAsync(req.RuleSetId, ct);
        await Send.NoContentAsync(ct);
    }
}
