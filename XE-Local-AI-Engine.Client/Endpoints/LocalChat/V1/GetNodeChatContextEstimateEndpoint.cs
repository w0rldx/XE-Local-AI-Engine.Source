namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Estimates the fixed parts of the next chat request (system prompt, offered tools, output reserve, safety margin)
///     for a model and agent before anything is sent.
/// </summary>
/// <remarks>Read-only, content-free, Operator-gated; 404 when the node does not know the model.</remarks>
public sealed class GetNodeChatContextEstimateEndpoint : Endpoint<GetNodeChatContextEstimateRequest, NodeChatContextWindowResponse>
{
    private readonly IChatContextEstimateService _estimateService;

    public GetNodeChatContextEstimateEndpoint(IChatContextEstimateService estimateService)
    {
        ArgumentNullException.ThrowIfNull(estimateService);
        _estimateService = estimateService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.ContextEstimate);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetNodeChatContextEstimateRequest req, CancellationToken ct)
    {
        var estimate = await _estimateService.EstimateAsync(new ChatContextEstimateRequest
            {
                ModelName = req.ModelName,
                AgentId = req.AgentId,
                UseLocalTools = req.UseLocalTools ?? true,
                MaxOutputTokens = req.MaxOutputTokens,
                NumCtx = req.NumCtx
            },
            ct);
        if (estimate is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(LocalChatMapper.ToResponse(estimate), ct);
    }
}
