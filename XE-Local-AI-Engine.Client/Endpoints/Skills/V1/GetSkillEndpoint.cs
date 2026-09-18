namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Skills.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class GetSkillEndpoint : Endpoint<GetSkillRequest, SkillResponse>
{
    private readonly IAgentSkillService _agentSkillService;

    public GetSkillEndpoint(IAgentSkillService agentSkillService)
    {
        ArgumentNullException.ThrowIfNull(agentSkillService);
        _agentSkillService = agentSkillService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Skills.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetSkillRequest req, CancellationToken ct)
    {
        var record = await _agentSkillService.GetByIdAsync(req.SkillId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
