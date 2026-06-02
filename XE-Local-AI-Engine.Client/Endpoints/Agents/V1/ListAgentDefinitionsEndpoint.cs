namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ListAgentDefinitionsEndpoint(IAgentDefinitionService agentDefinitionService)
    : EndpointWithoutRequest<ListAgentDefinitionsResponse>
{
    private readonly IAgentDefinitionService _agentDefinitionService = agentDefinitionService ?? throw new ArgumentNullException(nameof(agentDefinitionService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _agentDefinitionService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListAgentDefinitionsResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
