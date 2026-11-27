namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class DeleteSkillEndpoint : Endpoint<DeleteSkillRequest>
{
    private readonly AgentSkillService _agentSkillService;

    public DeleteSkillEndpoint(AgentSkillService agentSkillService)
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
        await _agentSkillService.DeleteAsync(req.SkillId, ct);

        await Send.NoContentAsync(ct);
    }
}
