namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     FastEndpoints handler for the delete agent definition local API operation.
/// </summary>
public sealed class DeleteAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService)
    : Endpoint<DeleteAgentDefinitionRequest>
{
    private readonly IAgentDefinitionService _agentDefinitionService = agentDefinitionService ?? throw new ArgumentNullException(nameof(agentDefinitionService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Agents.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteAgentDefinitionRequest req, CancellationToken ct)
    {
        var deleted = await _agentDefinitionService.DeleteAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
