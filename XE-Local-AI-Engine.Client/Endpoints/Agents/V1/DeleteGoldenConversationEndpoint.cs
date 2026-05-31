namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Playbook P4: deletes a golden conversation case (ownership-guarded — the service only deletes a case owned by the
///     agent named on the route, so one agent's route cannot touch another agent's case). 204 on delete; 404 when the
///     case is missing or belongs to another agent. Operator-gated.
/// </summary>
public sealed class DeleteGoldenConversationEndpoint(IGoldenConversationService goldenConversationService)
    : Endpoint<DeleteGoldenConversationRequest>
{
    private readonly IGoldenConversationService _goldenConversationService = goldenConversationService ?? throw new ArgumentNullException(nameof(goldenConversationService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Agents.GoldenConversation);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteGoldenConversationRequest req, CancellationToken ct)
    {
        var deleted = await _goldenConversationService.DeleteAsync(req.AgentDefinitionId, req.GoldenConversationId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
