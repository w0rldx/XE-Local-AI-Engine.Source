namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     analysis staging: edits a pending Suggested/Analysis action before review. The action stays Suggested/Analysis and
///     keeps its evidence + confidence; only the operator-editable fields change. 404 when the action is missing,
///     belongs to another agent, or is not a pending suggestion. Operator-gated. A separate route from the manual
///     PUT so analysis provenance is never rewritten to Manual.
/// </summary>
public sealed class UpdateSuggestedPlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<UpdateSuggestedPlaybookActionRequest, PlaybookActionResponse>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Put(LocalApiRoutes.Agents.PlaybookActionSuggested);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateSuggestedPlaybookActionRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _playbookActionService.UpdateSuggestedAsync(new SuggestedActionEditInput(req.AgentDefinitionId,
                    req.ActionId,
                    req.Behavior ?? string.Empty,
                    req.TriggerCondition,
                    req.Scope,
                    req.Priority),
                ct).ConfigureAwait(false);

            if (record is null)
            {
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            }

            await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (PlaybookActionValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
