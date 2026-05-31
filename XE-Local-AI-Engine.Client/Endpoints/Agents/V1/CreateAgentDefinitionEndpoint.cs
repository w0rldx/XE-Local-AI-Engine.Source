namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     FastEndpoints handler for the create agent definition local API operation.
/// </summary>
public sealed class CreateAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService)
    : Endpoint<CreateAgentDefinitionRequest, AgentDefinitionResponse>
{
    private readonly IAgentDefinitionService _agentDefinitionService = agentDefinitionService ?? throw new ArgumentNullException(nameof(agentDefinitionService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateAgentDefinitionRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _agentDefinitionService.CreateAsync(req.ToInput(), ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetAgentDefinitionEndpoint>(new
                {
                    agentDefinitionId = record.Id
                },
                record.ToResponse(),
                cancellation: ct).ConfigureAwait(false);
        }
        catch (AgentDefinitionValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
