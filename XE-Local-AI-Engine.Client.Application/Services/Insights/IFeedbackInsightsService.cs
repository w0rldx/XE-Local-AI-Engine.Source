namespace XE_Local_AI_Engine.Client.Services.Insights;

/// <summary>
///     Application-layer read model over the per-agent feedback aggregate. Shapes the raw store counts
///     into operator-facing analytics: derived totals/down-rate, the "never act on n=1" threshold flag, and
///     privacy-capped/truncated comment exemplars. Pure analytics — no generation, no playbook writes.
/// </summary>
public interface IFeedbackInsightsService
{
    /// <summary>
    ///     Returns the shaped feedback insights for the agent, or <c>null</c> when no agent definition has that id
    ///     (the endpoint maps <c>null</c> to 404).
    /// </summary>
    Task<FeedbackInsightsResult?> GetAgentFeedbackInsightsAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The per-agent feedback insights read model. <see cref="MinOccurrenceThreshold" /> is the "act on a pattern, never n=1" bar applied to <see cref="OverallFeedback.MeetsThreshold" /> and each
///     <see cref="ToolFeedbackBreakdown.MeetsThreshold" />.
/// </summary>
public sealed record FeedbackInsightsResult(
    Guid AgentDefinitionId,
    string AgentName,
    long GeneratedAtUtc,
    int MinOccurrenceThreshold,
    OverallFeedback Overall,
    IReadOnlyList<ToolFeedbackBreakdown> ByTool,
    IReadOnlyList<FeedbackExemplarView> Exemplars);

/// <summary>Overall up/down feedback for the agent. <see cref="DownRate" /> is <c>Down/Total</c> (0 when there is no feedback).</summary>
public sealed record OverallFeedback(int Total, int Up, int Down, double DownRate, bool MeetsThreshold);

/// <summary>Per-tool feedback breakdown (conversation-level attribution — see the store contract).</summary>
public sealed record ToolFeedbackBreakdown(string ToolName, int Total, int Up, int Down, double DownRate, bool MeetsThreshold);

/// <summary>A capped/truncated comment exemplar. <see cref="MessageId" />/<see cref="ConversationId" /> identify the feedback evidence used by analysis suggestions.</summary>
public sealed record FeedbackExemplarView(string Rating, string Comment, Guid MessageId, Guid ConversationId, long CreatedAtUtc, bool Truncated);
