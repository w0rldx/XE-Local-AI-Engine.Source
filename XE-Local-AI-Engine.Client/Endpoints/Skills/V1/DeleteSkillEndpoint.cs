namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class DeleteSkillEndpoint : Endpoint<DeleteSkillRequest>
{
    private readonly IAgentSkillService _agentSkillService;

    public DeleteSkillEndpoint(IAgentSkillService agentSkillService)
    {
        ArgumentNullException.ThrowIfNull(agentSkillService);
        _agentSkillService = agentSkillService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Skills.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteSkillRequest req, CancellationToken ct)
    {
        var deleted = await _agentSkillService.DeleteAsync(req.SkillId, ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
