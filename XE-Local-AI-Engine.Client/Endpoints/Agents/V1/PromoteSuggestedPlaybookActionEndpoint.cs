namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Playbook P3: promotes a pending Suggested/Analysis action to Enabled (human approval — staging ≠ active).
///     404 when the action is missing, belongs to another agent, or is not a pending suggestion. Operator-gated.
/// </summary>
public sealed class PromoteSuggestedPlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<SuggestedPlaybookActionRouteRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.PlaybookActionPromote);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SuggestedPlaybookActionRouteRequest req, CancellationToken ct)
    {
        var record = await _playbookActionService.PromoteSuggestedAsync(req.AgentDefinitionId, req.ActionId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
