namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Insights;

/// <summary>
///     Read-only per-agent feedback insights, aggregating the node-local message feedback the chat path already
///     persists. Operator-gated.
/// </summary>
/// <remarks>No writes, so no mutation guard. Returns 404 when the agent definition does not exist.</remarks>
public sealed class GetAgentFeedbackInsightsEndpoint : Endpoint<GetAgentFeedbackInsightsRequest, AgentFeedbackInsightsResponse>
{
    private readonly IFeedbackInsightsService _feedbackInsightsService;

    public GetAgentFeedbackInsightsEndpoint(IFeedbackInsightsService feedbackInsightsService)
    {
        ArgumentNullException.ThrowIfNull(feedbackInsightsService);
        _feedbackInsightsService = feedbackInsightsService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.FeedbackInsights);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetAgentFeedbackInsightsRequest req, CancellationToken ct)
    {
        var result = await _feedbackInsightsService.GetAgentFeedbackInsightsAsync(req.AgentDefinitionId, ct);
        if (result is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(result.ToResponse(), ct);
    }
}
