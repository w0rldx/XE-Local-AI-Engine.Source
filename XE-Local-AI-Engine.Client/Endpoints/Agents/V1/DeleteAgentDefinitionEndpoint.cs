namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class DeleteAgentDefinitionEndpoint : Endpoint<DeleteAgentDefinitionRequest>
{
    private readonly IAgentDefinitionService _agentDefinitionService;

    public DeleteAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService)
    {
        ArgumentNullException.ThrowIfNull(agentDefinitionService);
        _agentDefinitionService = agentDefinitionService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Agents.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteAgentDefinitionRequest req, CancellationToken ct)
    {
        await _agentDefinitionService.DeleteAsync(req.AgentDefinitionId, ct);

        await Send.NoContentAsync(ct);
    }
}
