namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Analysis staging: edits a pending Suggested/Analysis action before review — it stays Suggested/Analysis and
///     keeps its evidence + confidence, and only the operator-editable fields change.
/// </summary>
/// <remarks>
///     A separate route from the manual PUT so analysis provenance is never rewritten to Manual. Operator-gated; 404
///     when the action is missing, belongs to another agent, or is not a pending suggestion.
/// </remarks>
public sealed class UpdateSuggestedPlaybookActionEndpoint : Endpoint<UpdateSuggestedPlaybookActionRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService;

    public UpdateSuggestedPlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    {
        ArgumentNullException.ThrowIfNull(playbookActionService);
        _playbookActionService = playbookActionService;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Agents.PlaybookActionSuggested);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateSuggestedPlaybookActionRequest req, CancellationToken ct)
    {
        var record = await _playbookActionService.UpdateSuggestedAsync(new SuggestedActionEditInput
        {
            AgentDefinitionId = req.AgentDefinitionId,
            ActionId = req.ActionId,
            Behavior = req.Behavior ?? string.Empty,
            TriggerCondition = req.TriggerCondition,
            Scope = req.Scope,
            Priority = req.Priority
        },
            ct);

        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
