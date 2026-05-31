namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Playbook P4: runs the eval gate over one agent's golden conversation set for a pending Suggested/Analysis action
///     and persists the resulting <c>EvalResult</c> on the action (the promote gate reads it back). Returns the updated
///     action (now carrying <c>evalResult</c>); 404 when the action is missing, belongs to another agent, or is not a
///     pending suggestion. The route carries the ids so the body is empty <c>{}</c> (FastEndpoints 415s a route-only POST
///     with no body). Operator-gated.
/// </summary>
public sealed class RunPlaybookActionEvalEndpoint(IPlaybookEvalService playbookEvalService)
    : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookEvalService _playbookEvalService = playbookEvalService ?? throw new ArgumentNullException(nameof(playbookEvalService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionEval);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        var outcome = await _playbookEvalService.RunEvalAsync(req.AgentDefinitionId, req.ActionId, ct).ConfigureAwait(false);

        // The service enforced ownership, persisted EvalResult, and returned the updated record on the outcome — map it
        // directly. A missing record (ActionFound == false, or the ownership-guarded record returned null) is a 404; no
        // second, unscoped re-fetch.
        if (!outcome.ActionFound || outcome.Action is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(outcome.Action.ToResponse(), ct).ConfigureAwait(false);
    }
}
