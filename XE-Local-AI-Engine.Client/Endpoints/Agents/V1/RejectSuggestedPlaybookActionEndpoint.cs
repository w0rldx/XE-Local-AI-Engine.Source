namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     analysis staging: rejects a pending Suggested/Analysis action by archiving it (provenance preserved). 404 when the
///     action is missing or belongs to another agent, 409 when it is not a pending suggestion. Operator-gated.
/// </summary>
public sealed class RejectSuggestedPlaybookActionEndpoint : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService;

    public RejectSuggestedPlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    {
        ArgumentNullException.ThrowIfNull(playbookActionService);
        _playbookActionService = playbookActionService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionReject);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: no body means no Content-Type, and the default POST "Accepts" metadata (application/json
        // only) would answer 415. Accepting any content-type lets the request through; the ids still bind from the route.
        Description(x => x.Accepts<SuggestedPlaybookActionRouteRequest>()
                          .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        switch (await _playbookActionService.RejectSuggestedAsync(req.AgentDefinitionId, req.ActionId, ct))
        {
            case PlaybookSuggestionReviewResult.Applied applied:
                await Send.OkAsync(applied.Record.ToResponse(), ct);
                return;
            default:
                await Send.ConflictProblemAsync("The action is no longer a pending suggestion.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["outcome"] = "NotPending"
                    });
                return;
        }
    }
}
