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
public sealed class FeedbackInsightsResult
{
    public required Guid AgentDefinitionId { get; init; }

    public required string AgentName { get; init; }

    public required long GeneratedAtUtc { get; init; }

    public required int MinOccurrenceThreshold { get; init; }

    public required OverallFeedback Overall { get; init; }

    public required IReadOnlyList<ToolFeedbackBreakdown> ByTool { get; init; }

    public required IReadOnlyList<FeedbackExemplarView> Exemplars { get; init; }
}

/// <summary>Overall up/down feedback for the agent. <see cref="DownRate" /> is <c>Down/Total</c> (0 when there is no feedback).</summary>
public sealed class OverallFeedback
{
    public required int Total { get; init; }

    public required int Up { get; init; }

    public required int Down { get; init; }

    public required double DownRate { get; init; }

    public required bool MeetsThreshold { get; init; }
}

/// <summary>Per-tool feedback breakdown (conversation-level attribution — see the store contract).</summary>
public sealed class ToolFeedbackBreakdown
{
    public required string ToolName { get; init; }

    public required int Total { get; init; }

    public required int Up { get; init; }

    public required int Down { get; init; }

    public required double DownRate { get; init; }

    public required bool MeetsThreshold { get; init; }
}

/// <summary>A capped/truncated comment exemplar. <see cref="MessageId" />/<see cref="ConversationId" /> identify the feedback evidence used by analysis suggestions.</summary>
public sealed class FeedbackExemplarView
{
    public required string Rating { get; init; }

    public required string Comment { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required bool Truncated { get; init; }
}
