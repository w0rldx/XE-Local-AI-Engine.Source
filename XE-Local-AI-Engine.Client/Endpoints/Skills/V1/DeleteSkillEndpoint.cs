namespace XE_Local_AI_Engine.Client.Endpoints.Skills.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class DeleteSkillEndpoint(IAgentSkillService agentSkillService)
    : Endpoint<DeleteSkillRequest>
{
    private readonly IAgentSkillService _agentSkillService = agentSkillService ?? throw new ArgumentNullException(nameof(agentSkillService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Skills.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteSkillRequest req, CancellationToken ct)
    {
        var deleted = await _agentSkillService.DeleteAsync(req.SkillId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
