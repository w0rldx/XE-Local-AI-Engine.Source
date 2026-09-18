namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class GetAgentDefinitionEndpoint : Endpoint<GetAgentDefinitionRequest, AgentDefinitionResponse>
{
    private readonly IAgentDefinitionService _agentDefinitionService;

    public GetAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService)
    {
        ArgumentNullException.ThrowIfNull(agentDefinitionService);
        _agentDefinitionService = agentDefinitionService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetAgentDefinitionRequest req, CancellationToken ct)
    {
        var record = await _agentDefinitionService.GetByIdAsync(req.AgentDefinitionId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
