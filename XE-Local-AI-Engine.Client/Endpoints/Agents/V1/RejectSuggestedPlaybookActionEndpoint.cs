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
public sealed class RejectSuggestedPlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionReject);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST: the agent and action ids come from the route, so a well-behaved client sends no body — and
        // therefore no Content-Type. The default POST "Accepts" metadata only allows application/json, which
        // FastEndpoints answers with 415 when the header is absent. Overriding Accepts to accept any content-type lets a
        // body-less request through (the ids still bind from the route).
        Description(x => x.Accepts<SuggestedPlaybookActionRouteRequest>());
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        var record = await _playbookActionService.RejectSuggestedAsync(req.AgentDefinitionId, req.ActionId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
