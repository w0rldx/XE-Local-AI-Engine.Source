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
///     when the action is missing or belongs to another agent, 409 when it is not a pending suggestion.
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
        Description(builder => builder.ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateSuggestedPlaybookActionRequest req, CancellationToken ct)
    {
        var result = await _playbookActionService.UpdateSuggestedAsync(new SuggestedActionEditInput
            {
                AgentDefinitionId = req.AgentDefinitionId,
                ActionId = req.ActionId,
                Behavior = req.Behavior ?? string.Empty,
                TriggerCondition = req.TriggerCondition,
                Scope = req.Scope,
                Priority = req.Priority
            },
            ct);

        switch (result)
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
