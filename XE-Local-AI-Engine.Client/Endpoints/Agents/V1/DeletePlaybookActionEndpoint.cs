namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     FastEndpoints handler for the delete playbook action local API operation.
/// </summary>
public sealed class DeletePlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<DeletePlaybookActionRequest>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Agents.PlaybookActionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeletePlaybookActionRequest req, CancellationToken ct)
    {
        var deleted = await _playbookActionService.DeleteAsync(req.AgentDefinitionId, req.ActionId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
