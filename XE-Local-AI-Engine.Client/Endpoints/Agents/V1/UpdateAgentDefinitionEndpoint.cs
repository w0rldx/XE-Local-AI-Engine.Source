namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class UpdateAgentDefinitionEndpoint : Endpoint<UpdateAgentDefinitionRequest, AgentDefinitionResponse>
{
    private readonly IAgentDefinitionService _agentDefinitionService;
    private readonly TimeProvider _timeProvider;

    public UpdateAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(agentDefinitionService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _agentDefinitionService = agentDefinitionService;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Agents.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateAgentDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var record = await _agentDefinitionService.UpdateAsync(req.AgentDefinitionId, req.ToInput(_timeProvider.GetUtcNow()), ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
