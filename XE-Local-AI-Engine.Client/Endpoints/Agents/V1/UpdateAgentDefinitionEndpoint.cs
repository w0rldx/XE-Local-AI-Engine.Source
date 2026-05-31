namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     FastEndpoints handler for the update agent definition local API operation.
/// </summary>
public sealed class UpdateAgentDefinitionEndpoint(IAgentDefinitionService agentDefinitionService)
    : Endpoint<UpdateAgentDefinitionRequest, AgentDefinitionResponse>
{
    private readonly IAgentDefinitionService _agentDefinitionService = agentDefinitionService ?? throw new ArgumentNullException(nameof(agentDefinitionService));

    public override void Configure()
    {
        Put(LocalApiRoutes.Agents.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateAgentDefinitionRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _agentDefinitionService.UpdateAsync(req.AgentDefinitionId, req.ToInput(), ct).ConfigureAwait(false);
            if (record is null)
            {
                await Send.NotFoundAsync(ct).ConfigureAwait(false);
                return;
            }

            await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (AgentDefinitionValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
