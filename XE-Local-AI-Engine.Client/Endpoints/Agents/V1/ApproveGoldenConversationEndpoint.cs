namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Promotes a staged harvested golden candidate into the active set (flips it to <c>Enabled == true</c>). A
///     route-only POST, so the client posts <c>{}</c>. Operator-gated.
/// </summary>
/// <remarks>
///     Ownership-guarded: the service only enables a harvested, currently-disabled case owned by the agent named on
///     the route, so one agent's route cannot touch another agent's case. 200 with the updated case; 404 when the case
///     is missing or belongs to another agent; 409 when it is manual or already enabled.
/// </remarks>
public sealed class ApproveGoldenConversationEndpoint : Endpoint<ApproveGoldenConversationRequest, GoldenConversationResponse>
{
    private readonly IGoldenConversationService _goldenConversationService;

    public ApproveGoldenConversationEndpoint(IGoldenConversationService goldenConversationService)
    {
        ArgumentNullException.ThrowIfNull(goldenConversationService);
        _goldenConversationService = goldenConversationService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Agents.GoldenConversationApprove);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ApproveGoldenConversationRequest req, CancellationToken ct)
    {
        var result = await _goldenConversationService.ApproveHarvestedAsync(req.AgentDefinitionId, req.GoldenConversationId, ct);
        if (result is GoldenConversationApproveResult.Approved approved)
        {
            await Send.OkAsync(approved.Record.ToResponse(), ct);
            return;
        }

        // The `outcome` member keeps the refusal machine-readable without matching on the message.
        var alreadyEnabled = result is GoldenConversationApproveResult.AlreadyEnabled;
        await Send.ConflictProblemAsync(alreadyEnabled
                ? "The case is already in the active golden set."
                : "Only a harvested candidate can be approved; this case was authored manually.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["outcome"] = alreadyEnabled ? "AlreadyEnabled" : "NotHarvested"
            });
    }
}
