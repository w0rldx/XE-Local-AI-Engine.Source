namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CreateAgentDefinitionEndpoint : Endpoint<CreateAgentDefinitionRequest, AgentDefinitionResponse>
{
    private readonly IAgentDefinitionService _agentDefinitionService;
    private readonly TimeProvider _timeProvider;

    public CreateAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(agentDefinitionService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _agentDefinitionService = agentDefinitionService;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateAgentDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var record = await _agentDefinitionService.CreateAsync(req.ToInput(_timeProvider.GetUtcNow()), ct);
        await Send.CreatedAtAsync<GetAgentDefinitionEndpoint>(new
            {
                agentDefinitionId = record.Id
            },
            record.ToResponse(),
            cancellation: ct);
    }
}
