namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class CreateSkillEndpoint : Endpoint<CreateSkillRequest, SkillResponse>
{
    private readonly IAgentSkillService _agentSkillService;
    private readonly TimeProvider _timeProvider;

    public CreateSkillEndpoint(IAgentSkillService agentSkillService, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(agentSkillService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _agentSkillService = agentSkillService;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Skills.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateSkillRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var record = await _agentSkillService.CreateAsync(req.ToInput(_timeProvider.GetUtcNow()), ct);
        await Send.CreatedAtAsync<GetSkillEndpoint>(new
            {
                skillId = record.Id
            },
            record.ToResponse(),
            cancellation: ct);
    }
}
