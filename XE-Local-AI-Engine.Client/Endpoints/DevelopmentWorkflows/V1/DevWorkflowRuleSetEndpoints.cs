namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
        await Send.OkAsync(new ListDevWorkflowRuleSetsResponse { Items = [.. ruleSets.Select(DevWorkflowContractMapper.ToResponse)] }, ct);
    }
}

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

/// <summary>
///     Replaces the whole document, refusing an edit made against a version that has since moved on.
///     <para>
///         Editing a rule set while a run is in flight is allowed, and deliberately: each node run recorded the
///         <c>{id, name, contentSha256}</c> that applied to it, so the audit keeps naming the exact text it was given
///         and the hash is what says the current document is no longer that text.
///     </para>
/// </summary>
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

/// <summary>
///     A HARD delete, unlike a definition's archive, and it does not refuse while a run is in flight. Nothing holds a
///     foreign key to a rule set, and what a node run needs from one — which document applied, at which text — it
///     copied onto its own row at materialization. The objective composer skips a document that is gone.
/// </summary>
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
