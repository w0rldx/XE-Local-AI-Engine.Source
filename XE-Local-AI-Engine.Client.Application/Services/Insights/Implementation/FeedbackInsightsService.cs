namespace XE_Local_AI_Engine.Client.Services.Insights.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class FeedbackInsightsService : IFeedbackInsightsService
{
    /// <summary>Minimum occurrences before a signal is treated as a pattern rather than a one-off (n=1).</summary>
    internal const int MinOccurrenceThreshold = 3;

    /// <summary>Maximum comment exemplars surfaced per agent (privacy cap).</summary>
    internal const int MaxExemplars = 5;

    /// <summary>Maximum exemplar comment length before truncation (privacy cap).</summary>
    internal const int MaxExemplarCommentLength = 280;

    private readonly IFeedbackInsightsStore _store;
    private readonly TimeProvider _timeProvider;

    public FeedbackInsightsService(IFeedbackInsightsStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<FeedbackInsightsResult?> GetAgentFeedbackInsightsAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var aggregate = await _store.GetAgentFeedbackAggregateAsync(agentDefinitionId, MaxExemplars, cancellationToken);
        if (aggregate is null)
        {
            return null;
        }

        var byTool = aggregate.ByTool.Select(BuildToolBreakdown).ToArray();
        var exemplars = aggregate.Exemplars.Select(BuildExemplar).ToArray();

        return new FeedbackInsightsResult(aggregate.AgentDefinitionId,
            aggregate.AgentName,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            MinOccurrenceThreshold,
            BuildOverall(aggregate.UpCount, aggregate.DownCount),
            byTool,
            exemplars);
    }

    private static OverallFeedback BuildOverall(int up, int down)
    {
        var total = up + down;
        return new OverallFeedback(total, up, down, DownRate(down, total), total >= MinOccurrenceThreshold);
    }

    private static ToolFeedbackBreakdown BuildToolBreakdown(ToolFeedbackCount tool)
    {
        var total = tool.UpCount + tool.DownCount;
        return new ToolFeedbackBreakdown(tool.ToolName, total, tool.UpCount, tool.DownCount, DownRate(tool.DownCount, total), total >= MinOccurrenceThreshold);
    }

    private static FeedbackExemplarView BuildExemplar(FeedbackExemplar exemplar)
    {
        var truncated = exemplar.Comment.Length > MaxExemplarCommentLength;
        var comment = truncated ? Truncate(exemplar.Comment) : exemplar.Comment;
        return new FeedbackExemplarView(exemplar.Rating, comment, exemplar.MessageId, exemplar.ConversationId, exemplar.CreatedAtUtc, truncated);
    }

    private static string Truncate(string comment)
    {
        // Don't slice through a surrogate pair at the boundary — a lone surrogate would serialize to U+FFFD.
        var cut = MaxExemplarCommentLength;
        if (char.IsHighSurrogate(comment[cut - 1]))
        {
            cut--;
        }

        return string.Concat(comment.AsSpan(start: 0, cut), "…");
    }

    private static double DownRate(int down, int total)
    {
        return total == 0 ? 0d : Math.Round(down / (double)total, digits: 4);
    }
}
