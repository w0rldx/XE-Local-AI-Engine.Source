namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Insights;

/// <summary>
///     Read-only per-agent feedback insights (feedback insights). Aggregates the node-local message feedback already
///     persisted by the chat path — no writes, so no mutation guard. Operator-gated. Returns 404 when the agent
///     definition does not exist.
/// </summary>
public sealed class GetAgentFeedbackInsightsEndpoint(IFeedbackInsightsService feedbackInsightsService)
    : Endpoint<GetAgentFeedbackInsightsRequest, AgentFeedbackInsightsResponse>
{
    private readonly IFeedbackInsightsService _feedbackInsightsService = feedbackInsightsService ?? throw new ArgumentNullException(nameof(feedbackInsightsService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Agents.FeedbackInsights);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetAgentFeedbackInsightsRequest req, CancellationToken ct)
    {
        var result = await _feedbackInsightsService.GetAgentFeedbackInsightsAsync(req.AgentDefinitionId, ct).ConfigureAwait(false);
        if (result is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(result.ToResponse(), ct).ConfigureAwait(false);
    }
}
