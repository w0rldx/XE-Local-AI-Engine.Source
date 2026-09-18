namespace XE_Local_AI_Engine.Client.Services.Agents;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The Agents Operator endpoints' only read door onto the append-only execution-log table: the per-agent
///     diagnostics page, the run-envelope page and the token-usage aggregate. An endpoint is the HTTP edge and may
///     not reach into a store itself (the endpoint-dependency rule), so every read the store already filters by
///     record kind arrives here unchanged — this type adds no policy of its own and owns no writes.
/// </summary>
public sealed class AgentExecutionLogQueryService
{
    private readonly IAgentExecutionLogStore _executionLogs;

    public AgentExecutionLogQueryService(IAgentExecutionLogStore executionLogs)
    {
        ArgumentNullException.ThrowIfNull(executionLogs);
        _executionLogs = executionLogs;
    }

    /// <summary>
    ///     Returns a page of adaptive-memory diagnostics rows for <paramref name="agentDefinitionId" />, newest first.
    /// </summary>
    public Task<IReadOnlyList<AgentExecutionLogRecord>> ListByAgentAsync(Guid agentDefinitionId,
        int limit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return _executionLogs.ListByAgentAsync(agentDefinitionId, limit, offset, cancellationToken);
    }

    /// <summary>
    ///     Returns the run-envelope token-usage buckets over the optional half-open epoch-millisecond range.
    /// </summary>
    public Task<IReadOnlyList<TokenUsageAggregateRecord>> SummarizeTokenUsageAsync(long? fromEpochMsInclusive,
        long? toEpochMsExclusive,
        CancellationToken cancellationToken = default)
    {
        return _executionLogs.SummarizeTokenUsageAsync(fromEpochMsInclusive, toEpochMsExclusive, cancellationToken);
    }

    /// <summary>
    ///     Returns a page of durable run-envelope rows, newest first, optionally scoped to one conversation.
    /// </summary>
    public Task<IReadOnlyList<AgentRunEnvelopeRecord>> ListRunEnvelopesAsync(Guid? conversationId,
        int limit,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return _executionLogs.ListRunEnvelopesAsync(conversationId, limit, offset, cancellationToken);
    }
}
