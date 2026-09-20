namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Runs golden-conversation evaluation over one agent's golden set for a pending Suggested/Analysis action and
///     persists the resulting <c>EvalResult</c> on the action, which the promote route reads back. Operator-gated.
/// </summary>
/// <remarks>
///     Returns the updated action, now carrying <c>evalResult</c>; 404 when the action is missing, belongs to another
///     agent, or is not a pending suggestion. The route carries the ids so the request is body-less, and
///     <c>Configure</c> overrides Accepts so the missing Content-Type is not answered with 415.
/// </remarks>
public sealed class RunPlaybookActionEvalEndpoint : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookEvalService _playbookEvalService;

    public RunPlaybookActionEvalEndpoint(IPlaybookEvalService playbookEvalService)
    {
        ArgumentNullException.ThrowIfNull(playbookEvalService);
        _playbookEvalService = playbookEvalService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionEval);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: no body means no Content-Type, and the default POST "Accepts" metadata (application/json
        // only) would answer 415. Accepting any content-type lets the request through; the ids still bind from the route.
        Description(x => x.Accepts<SuggestedPlaybookActionRouteRequest>());
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        var outcome = await _playbookEvalService.RunEvalAsync(req.AgentDefinitionId, req.ActionId, ct);

        // The service enforced ownership, persisted EvalResult and returned the updated record on the outcome — map it directly. A missing record (no ActionFound,
        // or the ownership-guarded record returned null) is a 404; no second, unscoped re-fetch.
        if (!outcome.ActionFound || outcome.Action is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(outcome.Action.ToResponse(), ct);
    }
}
