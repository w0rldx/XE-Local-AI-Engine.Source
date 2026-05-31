namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Read-only aggregate over the node-local per-message feedback (Playbook P2). Groups the
///     <c>message_feedback</c> rows already persisted by the chat path (joined to
///     <c>conversations.agent_definition_id</c>) into a per-agent shape so recurring patterns surface for the
///     operator. Pure analytics: no feedback is collected here and no playbook action is written.
/// </summary>
public interface IFeedbackInsightsStore
{
    /// <summary>
    ///     Returns the per-agent feedback aggregate — overall up/down counts, a per-tool breakdown, and up to
    ///     <paramref name="exemplarCap" /> comment exemplars — or <c>null</c> when no agent definition has
    ///     <paramref name="agentDefinitionId" />. Only feedback on conversations bound to that agent
    ///     (<c>agent_definition_id</c>) and not purged is counted; archived conversations are included. All columns
    ///     read are plaintext, so no decryption is involved.
    /// </summary>
    Task<AgentFeedbackAggregate?> GetAgentFeedbackAggregateAsync(Guid agentDefinitionId, int exemplarCap, CancellationToken cancellationToken = default);
}

/// <summary>Raw per-agent feedback aggregate (plaintext; no encrypted columns are read).</summary>
public sealed record AgentFeedbackAggregate(
    Guid AgentDefinitionId,
    string AgentName,
    int UpCount,
    int DownCount,
    IReadOnlyList<ToolFeedbackCount> ByTool,
    IReadOnlyList<FeedbackExemplar> Exemplars);

/// <summary>
///     Up/down feedback counts attributed to a tool. Attribution is <b>conversation-level</b>: a feedback row is
///     counted for tool X when the conversation it belongs to recorded at least one <c>tool_events</c> row for X
///     (<c>tool_events</c> has no message link). Counts use <c>COUNT(DISTINCT message_id)</c> so a conversation that
///     used a tool many times still counts each rated message once.
/// </summary>
public sealed record ToolFeedbackCount(string ToolName, int UpCount, int DownCount);

/// <summary>
///     A single feedback comment exemplar with its evidence refs. <see cref="MessageId" /> / <see cref="ConversationId" />
///     are the references the deferred analysis phase (P3) cites as source feedback; <see cref="Comment" /> is the raw
///     stored comment (truncation/capping is applied by the application service, not here).
/// </summary>
public sealed record FeedbackExemplar(string Rating, string Comment, Guid MessageId, Guid ConversationId, long CreatedAtUtc);
