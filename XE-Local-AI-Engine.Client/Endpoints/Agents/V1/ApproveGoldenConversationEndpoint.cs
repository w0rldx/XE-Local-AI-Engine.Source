namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Harvest follow-up: promotes a staged harvested golden candidate into the active set (flips it to
///     <c>Enabled == true</c>). Ownership-guarded — the service only enables a harvested, currently-disabled case owned by
///     the agent named on the route, so one agent's route cannot touch another agent's case. 200 with the updated case;
///     404 when the case is missing, already enabled, manual, or belongs to another agent. A route-only POST (the client
///     posts <c>{}</c>). Operator-gated.
/// </summary>
public sealed class ApproveGoldenConversationEndpoint(IGoldenConversationService goldenConversationService)
    : Endpoint<ApproveGoldenConversationRequest, GoldenConversationResponse>
{
    private readonly IGoldenConversationService _goldenConversationService = goldenConversationService ?? throw new ArgumentNullException(nameof(goldenConversationService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.GoldenConversationApprove);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ApproveGoldenConversationRequest req, CancellationToken ct)
    {
        var record = await _goldenConversationService.ApproveHarvestedAsync(req.AgentDefinitionId, req.GoldenConversationId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
