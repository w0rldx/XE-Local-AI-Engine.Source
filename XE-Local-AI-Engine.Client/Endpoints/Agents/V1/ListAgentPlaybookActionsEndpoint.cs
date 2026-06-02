namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ListAgentPlaybookActionsEndpoint(IPlaybookActionService playbookActionService)
    : Endpoint<ListAgentPlaybookActionsRequest, ListPlaybookActionsResponse>
{
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.Playbook);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListAgentPlaybookActionsRequest req, CancellationToken ct)
    {
        var records = await _playbookActionService.ListByAgentAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListPlaybookActionsResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
