namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CreateSkillEndpoint(IAgentSkillService agentSkillService)
    : Endpoint<CreateSkillRequest, SkillResponse>
{
    private readonly IAgentSkillService _agentSkillService = agentSkillService ?? throw new ArgumentNullException(nameof(agentSkillService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Skills.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateSkillRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _agentSkillService.CreateAsync(req.ToInput(), ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetSkillEndpoint>(new
                {
                    skillId = record.Id
                },
                record.ToResponse(),
                cancellation: ct).ConfigureAwait(false);
        }
        catch (AgentSkillValidationException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
