namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Analysis, eval, and monitoring workflows: promotes a pending Suggested/Analysis action to Enabled (human approval — staging ≠ active),
///     gated by the golden-conversation eval result and the enabled-action cap. 404 when the action is missing, belongs to another
///     agent, or is not a pending suggestion; 409 when the eval has not passed (required / regressed / stale) or the
///     agent is already at the enabled-action cap (CapReached). Operator-gated.
/// </summary>
public sealed class PromoteSuggestedPlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionPromote);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the agent and action ids come from the route, so a well-behaved client sends no body — and
        // therefore no Content-Type. The default POST "Accepts" metadata only allows application/json, which
        // FastEndpoints answers with 415 when the header is absent. Overriding Accepts to accept any content-type lets a
        // body-less request through (the ids still bind from the route).
        Description(x => x.Accepts<SuggestedPlaybookActionRouteRequest>());
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        var result = await _playbookActionService.PromoteSuggestedAsync(req.AgentDefinitionId, req.ActionId, ct).ConfigureAwait(false);

        switch (result.Status)
        {
            case PlaybookPromotionStatus.Promoted when result.Record is not null:
                await Send.OkAsync(result.Record.ToResponse(), ct).ConfigureAwait(false);
                return;
            case PlaybookPromotionStatus.NotFound:
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            case PlaybookPromotionStatus.CapReached:
                // relevance retrieval and cohort monitoring hard cap: the agent is already at MaxEnabledActions. Surface a typed 409 with the
                // PascalCase status name (the established wire format every other branch uses) so the panel's parser
                // recognizes it and can explain the block and prompt an archive/disable.
                var capConflict = new PlaybookPromotionConflictResponse(result.Status.ToString(), ReasonFor(result.Status));
                await Send.ResultAsync(Results.Conflict(capConflict)).ConfigureAwait(false);
                return;
            default:
                // EvalRequired / EvalRegressed / EvalStale (and a Promoted with no record) → evaluation blocked the
                // promotion. Surface a typed 409 so the panel can explain why Approve is unavailable (same Conflict-body
                // convention as the chat/auth endpoints).
                var conflict = new PlaybookPromotionConflictResponse(result.Status.ToString(), ReasonFor(result.Status));
                await Send.ResultAsync(Results.Conflict(conflict)).ConfigureAwait(false);
                return;
        }
    }

    private static string ReasonFor(PlaybookPromotionStatus status)
    {
        return status switch
        {
            PlaybookPromotionStatus.EvalRequired => "Run the eval before promoting — no eval has run since this action was authored or edited.",
            PlaybookPromotionStatus.EvalRegressed => "The latest eval regressed at least one golden case. Resolve the regression before promoting.",
            PlaybookPromotionStatus.EvalStale => "The action changed since the last eval. Re-run the eval before promoting.",
            PlaybookPromotionStatus.CapReached => "The agent already has the maximum number of enabled playbook actions; archive or disable one before promoting.",
            _ => "Promotion is blocked by the eval gate."
        };
    }
}
