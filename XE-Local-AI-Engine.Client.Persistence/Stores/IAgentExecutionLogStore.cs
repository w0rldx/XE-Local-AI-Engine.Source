namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Node-scoped, append-only persistence for agent execution telemetry (adaptive memory diagnostics). Rows hold
///     metadata only — latency/tokens/success/errorClass/configHash plus link ids — and are NEVER encrypted; no message
///     content is stored here. The store owns id/timestamp stamping; it performs no content validation.
/// </summary>
public interface IAgentExecutionLogStore
{
    /// <summary>
    ///     Appends a new execution-log row (assigning <c>Id</c> and <c>CreatedAtUtc</c>) and returns the stored record.
    /// </summary>
    Task<AgentExecutionLogRecord> AddAsync(AgentExecutionLogInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns a page of execution logs for <paramref name="agentDefinitionId" />, newest first, capped to
    ///     <paramref name="limit" /> rows and skipping <paramref name="offset" /> rows.
    /// </summary>
    Task<IReadOnlyList<AgentExecutionLogRecord>> ListByAgentAsync(Guid agentDefinitionId, int limit, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retention sweep: deletes every log created before <paramref name="cutoffEpochMs" /> (matched on
    ///     <c>CreatedAtUtc</c>, unix-milliseconds) with a single set-based <c>ExecuteDeleteAsync</c> (no tracking).
    ///     Returns the number of rows deleted.
    /// </summary>
    Task<int> DeleteOlderThanAsync(long cutoffEpochMs, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Per-agent row cap: for every agent, keeps the newest <paramref name="maxPerAgent" /> logs and deletes the rest
    ///     with a single set-based <c>ExecuteDeleteAsync</c> (no tracking). A non-positive cap deletes nothing. Returns the
    ///     number of rows deleted.
    /// </summary>
    Task<int> TrimToMaxPerAgentAsync(int maxPerAgent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Typed projection of a persisted execution-log row. Metadata only — no message content. <see cref="ErrorClass" />
///     is an exception type name only (never the message text).
/// </summary>
public sealed record AgentExecutionLogRecord(
    Guid Id,
    Guid AgentDefinitionId,
    Guid? ConversationId,
    Guid? MessageId,
    string ModelName,
    string ConfigHash,
    long LatencyMs,
    int? PromptTokens,
    int? CompletionTokens,
    bool Success,
    string? ErrorClass,
    long CreatedAtUtc);

/// <summary>
///     Fields supplied when appending an execution-log row. Metadata only — supply NO message content here.
///     <see cref="ErrorClass" /> must be an exception type name only, never the exception message or transcript text.
/// </summary>
public sealed record AgentExecutionLogInput(
    Guid AgentDefinitionId,
    Guid? ConversationId,
    Guid? MessageId,
    string ModelName,
    string ConfigHash,
    long LatencyMs,
    bool Success,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? ErrorClass = null);
