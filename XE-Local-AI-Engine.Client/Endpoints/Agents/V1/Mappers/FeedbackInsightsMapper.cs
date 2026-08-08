namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Insights;

internal static class FeedbackInsightsMapper
{
    public static AgentFeedbackInsightsResponse ToResponse(this FeedbackInsightsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new AgentFeedbackInsightsResponse
        {
            AgentDefinitionId = result.AgentDefinitionId,
            AgentName = result.AgentName,
            GeneratedAtUtc = result.GeneratedAtUtc,
            MinOccurrenceThreshold = result.MinOccurrenceThreshold,
            Overall = new OverallFeedbackResponse
            {
                Total = result.Overall.Total,
                Up = result.Overall.Up,
                Down = result.Overall.Down,
                DownRate = result.Overall.DownRate,
                MeetsThreshold = result.Overall.MeetsThreshold
            },
            ByTool =
            [
                .. result.ByTool.Select(static tool => new ToolFeedbackResponse
                {
                    ToolName = tool.ToolName,
                    Total = tool.Total,
                    Up = tool.Up,
                    Down = tool.Down,
                    DownRate = tool.DownRate,
                    MeetsThreshold = tool.MeetsThreshold
                })
            ],
            Exemplars =
            [
                .. result.Exemplars.Select(static exemplar => new FeedbackExemplarResponse
                {
                    Rating = exemplar.Rating,
                    Comment = exemplar.Comment,
                    MessageId = exemplar.MessageId,
                    ConversationId = exemplar.ConversationId,
                    CreatedAtUtc = exemplar.CreatedAtUtc,
                    Truncated = exemplar.Truncated
                })
            ]
        };
    }
}
