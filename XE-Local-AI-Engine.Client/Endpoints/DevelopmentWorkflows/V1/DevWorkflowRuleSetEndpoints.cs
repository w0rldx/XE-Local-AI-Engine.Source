namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>The rule-set list. Never loads a body: it is the encrypted column, and the list has no use for it.</summary>
public sealed class ListDevWorkflowRuleSetsEndpoint(IDevWorkflowStore store) : EndpointWithoutRequest<ListDevWorkflowRuleSetsResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RuleSets);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var ruleSets = await _store.ListRuleSetsAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevWorkflowRuleSetsResponse([.. ruleSets.Select(DevWorkflowContractMapper.ToResponse)]), ct).ConfigureAwait(false);
    }
}

public sealed class CreateDevWorkflowRuleSetEndpoint(IDevWorkflowStore store) : Endpoint<CreateDevWorkflowRuleSetRequest, DevWorkflowRuleSetResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

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

        var created = await _store.CreateRuleSetAsync(new CreateDevWorkflowRuleSetCommand(Guid.NewGuid(),
                                          req.Name,
                                          req.Body,
                                          DevWorkflowContractMapper.ToScopeJson(req.Scope),
                                          req.Description,
                                          req.Enabled),
                                      ct)
                                  .ConfigureAwait(false);
        await Send.CreatedAtAsync<GetDevWorkflowRuleSetEndpoint>(new
            {
                ruleSetId = created.Id
            },
            created.ToResponse(),
            cancellation: ct).ConfigureAwait(false);
    }
}

public sealed class GetDevWorkflowRuleSetEndpoint(IDevWorkflowStore store) : Endpoint<DevWorkflowRuleSetRequest, DevWorkflowRuleSetResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.RuleSetById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowRuleSetRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var ruleSet = await _store.GetRuleSetAsync(req.RuleSetId, ct).ConfigureAwait(false);
        await Send.OkAsync(ruleSet.ToResponse(), ct).ConfigureAwait(false);
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
public sealed class UpdateDevWorkflowRuleSetEndpoint(IDevWorkflowStore store) : Endpoint<UpdateDevWorkflowRuleSetRequest, DevWorkflowRuleSetResponse>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

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

        var updated = await _store.UpdateRuleSetAsync(new UpdateDevWorkflowRuleSetCommand(req.RuleSetId,
                                          req.Version,
                                          req.Name,
                                          req.Body,
                                          DevWorkflowContractMapper.ToScopeJson(req.Scope),
                                          req.Description,
                                          req.Enabled),
                                      ct)
                                  .ConfigureAwait(false);
        await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     A HARD delete, unlike a definition's archive, and it does not refuse while a run is in flight. Nothing holds a
///     foreign key to a rule set, and what a node run needs from one — which document applied, at which text — it
///     copied onto its own row at materialization. The objective composer skips a document that is gone.
/// </summary>
public sealed class DeleteDevWorkflowRuleSetEndpoint(IDevWorkflowStore store) : Endpoint<DevWorkflowRuleSetRequest>
{
    private readonly IDevWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Delete(LocalApiRoutes.DevelopmentWorkflows.RuleSetById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevWorkflowRuleSetRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await _store.DeleteRuleSetAsync(req.RuleSetId, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
