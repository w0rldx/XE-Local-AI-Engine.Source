namespace XE_Local_AI_Engine.Client.Services.Insights.Implementation;

using XE_Local_AI_Engine.Client.Persistence;

internal sealed class FeedbackInsightsService(IFeedbackInsightsStore store, TimeProvider timeProvider) : IFeedbackInsightsService
{
    /// <summary>Minimum occurrences before a signal is "a pattern, not n=1" (Playbook doc §6 non-negotiable #1).</summary>
    internal const int MinOccurrenceThreshold = 3;

    /// <summary>Maximum comment exemplars surfaced per agent (privacy cap, §7).</summary>
    internal const int MaxExemplars = 5;

    /// <summary>Maximum exemplar comment length before truncation (privacy cap, §7).</summary>
    internal const int MaxExemplarCommentLength = 280;

    private readonly IFeedbackInsightsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<FeedbackInsightsResult?> GetAgentFeedbackInsightsAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        var aggregate = await _store.GetAgentFeedbackAggregateAsync(agentDefinitionId, MaxExemplars, cancellationToken).ConfigureAwait(false);
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

        return string.Concat(comment.AsSpan(0, cut), "…");
    }

    private static double DownRate(int down, int total)
    {
        return total == 0 ? 0d : Math.Round(down / (double)total, 4);
    }
}
