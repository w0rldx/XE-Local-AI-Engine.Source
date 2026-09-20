namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     analysis staging: rejects a pending Suggested/Analysis action by archiving it (provenance preserved). 404 when the
///     action is missing, belongs to another agent, or is not a pending suggestion. Operator-gated.
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
        Description(x => x.Accepts<SuggestedPlaybookActionRouteRequest>());
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        var record = await _playbookActionService.RejectSuggestedAsync(req.AgentDefinitionId, req.ActionId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
