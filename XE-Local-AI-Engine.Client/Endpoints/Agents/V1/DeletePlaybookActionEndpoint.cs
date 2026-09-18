namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class DeletePlaybookActionEndpoint : Endpoint<DeletePlaybookActionRequest>
{
    private readonly IPlaybookActionService _playbookActionService;

    public DeletePlaybookActionEndpoint(IPlaybookActionService playbookActionService)
    {
        ArgumentNullException.ThrowIfNull(playbookActionService);
        _playbookActionService = playbookActionService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Agents.PlaybookActionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeletePlaybookActionRequest req, CancellationToken ct)
    {
        var deleted = await _playbookActionService.DeleteAsync(req.AgentDefinitionId, req.ActionId, ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
