namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Runs golden-conversation evaluation over one agent's golden set for a pending Suggested/Analysis action and
///     persists the resulting <c>EvalResult</c> on the action (the promote route reads it back). Returns the updated
///     action (now carrying <c>evalResult</c>); 404 when the action is missing, belongs to another agent, or is not a
///     pending suggestion. The route carries the ids so the request is body-less (Configure overrides Accepts so the
///     missing Content-Type is not answered with 415). Operator-gated.
/// </summary>
public sealed class RunPlaybookActionEvalEndpoint(IPlaybookEvalService playbookEvalService)
    : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookEvalService _playbookEvalService = playbookEvalService ?? throw new ArgumentNullException(nameof(playbookEvalService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionEval);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the agent and action ids come from the route, so a well-behaved client sends no body — and
        // therefore no Content-Type. The default POST "Accepts" metadata only allows application/json, which
        // FastEndpoints answers with 415 when the header is absent. Overriding Accepts to accept any content-type lets a
        // body-less request through (the ids still bind from the route).
        Description(x => x.Accepts<SuggestedPlaybookActionRouteRequest>());
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
