namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ListSkillsEndpoint(IAgentSkillService agentSkillService)
    : EndpointWithoutRequest<ListSkillsResponse>
{
    private readonly IAgentSkillService _agentSkillService = agentSkillService ?? throw new ArgumentNullException(nameof(agentSkillService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Skills.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _agentSkillService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListSkillsResponse
            {
                Items = [.. records.Select(static record => record.ToSummary())]
            },
            ct).ConfigureAwait(false);
    }
}
