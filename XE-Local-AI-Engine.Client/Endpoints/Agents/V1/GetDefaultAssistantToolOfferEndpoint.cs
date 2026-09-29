namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Exposes the tool offer a chat turn hands the seeded Default Assistant on a given model, so the agent form renders
///     the server's list rather than re-deriving the capability, trust-boundary and node-switch gates.
/// </summary>
public sealed class GetDefaultAssistantToolOfferEndpoint : Endpoint<GetDefaultAssistantToolOfferRequest, DefaultAssistantToolOfferResponse>
{
    private readonly IDefaultAssistantToolOfferService _offerService;

    public GetDefaultAssistantToolOfferEndpoint(IDefaultAssistantToolOfferService offerService)
    {
        ArgumentNullException.ThrowIfNull(offerService);
        _offerService = offerService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.DefaultAssistantToolOffer);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetDefaultAssistantToolOfferRequest req, CancellationToken ct)
    {
        var offer = await _offerService.GetAsync(req.ModelName, ct);
        await Send.OkAsync(new DefaultAssistantToolOfferResponse
            {
                ModelName = offer.ModelName,
                ToolNames = offer.ToolNames
            },
            ct);
    }
}
