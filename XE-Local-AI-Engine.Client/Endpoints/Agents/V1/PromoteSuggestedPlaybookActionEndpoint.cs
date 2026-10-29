namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Analysis, eval and monitoring workflows: promotes a pending Suggested/Analysis action to Enabled (human
///     approval — staging ≠ active), gated by the golden-conversation eval result and the enabled-action cap.
/// </summary>
/// <remarks>
///     Operator-gated. 404 when the action is missing, belongs to another agent, or is not a pending suggestion; 409
///     when the eval has not passed (required / regressed / stale) or the agent is already at the enabled-action cap
///     (CapReached).
/// </remarks>
public sealed class PromoteSuggestedPlaybookActionEndpoint : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService;

    public PromoteSuggestedPlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    {
        ArgumentNullException.ThrowIfNull(playbookActionService);
        _playbookActionService = playbookActionService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionPromote);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: no body means no Content-Type, and the default POST "Accepts" metadata (application/json
        // only) would answer 415. Accepting any content-type lets the request through; the ids still bind from the route.
        Description(x => x.Accepts<SuggestedPlaybookActionRouteRequest>());
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        switch (await _playbookActionService.PromoteSuggestedAsync(req.AgentDefinitionId, req.ActionId, ct))
        {
            case PlaybookPromotionResult.Promoted promoted:
                await Send.OkAsync(promoted.Record.ToResponse(), ct);
                return;
            case PlaybookPromotionResult.Blocked blocked:
                // Not pending, an eval-gate block, or the enabled-action cap: a typed 409 with the PascalCase status name
                // (the established wire format) so the panel's parser recognizes it and explains why Approve is unavailable.
                await Send.ResultAsync(Results.Conflict(new PlaybookPromotionConflictResponse
                {
                    Status = blocked.Status.ToString(),
                    Reason = ReasonFor(blocked.Status)
                }));
                return;
        }
    }

    private static string ReasonFor(PlaybookPromotionStatus status)
    {
        return status switch
        {
            PlaybookPromotionStatus.EvalRequired => "Run the eval before promoting — no eval has run since this action was authored or edited.",
            PlaybookPromotionStatus.EvalRegressed => "The latest eval regressed at least one golden case. Resolve the regression before promoting.",
            PlaybookPromotionStatus.EvalStale =>
                "The action or its evaluation context (base instructions, sibling actions, golden set, or model) changed since the last eval. Re-run the eval before promoting.",
            PlaybookPromotionStatus.EvalIncomplete => "The last eval only evaluated a subset of the enabled golden cases. Raise the golden-case cap and re-run a complete eval before promoting.",
            PlaybookPromotionStatus.CapReached => "The agent already has the maximum number of enabled playbook actions; archive or disable one before promoting.",
            PlaybookPromotionStatus.NotPending => "The action is no longer a pending suggestion.",
            _ => "Promotion is blocked by the eval gate."
        };
    }
}
