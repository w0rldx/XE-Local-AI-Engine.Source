namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Deletes a golden conversation case (ownership-guarded — the service only deletes a case owned by the agent named
///     on the route, so one agent's route cannot touch another agent's case). 204 on delete; 404 when the
///     case is missing or belongs to another agent. Operator-gated.
/// </summary>
public sealed class DeleteGoldenConversationEndpoint : Endpoint<DeleteGoldenConversationRequest>
{
    private readonly IGoldenConversationService _goldenConversationService;

    public DeleteGoldenConversationEndpoint(IGoldenConversationService goldenConversationService)
    {
        ArgumentNullException.ThrowIfNull(goldenConversationService);
        _goldenConversationService = goldenConversationService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Agents.GoldenConversation);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteGoldenConversationRequest req, CancellationToken ct)
    {
        var deleted = await _goldenConversationService.DeleteAsync(req.AgentDefinitionId, req.GoldenConversationId, ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
