namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Append-only persistence boundary for agent execution telemetry. Writes metadata-only rows (no message content)
///     and reads them back paged, newest first.
/// </summary>
public sealed class AgentExecutionLogStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IAgentExecutionLogStore
{
    private const int ChatRunEnvelopeKind = (int)AgentExecutionLogRecordKind.ChatRunEnvelope;
    private const long MillisecondsPerDay = 86_400_000L;

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<AgentExecutionLogRecord> AddAsync(AgentExecutionLogInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = new AgentExecutionLog
        {
            Id = Guid.NewGuid(),
            RecordKind = (int)AgentExecutionLogRecordKind.AdaptiveMemoryDiagnostics,
            SchemaVersion = 0,
            AgentDefinitionId = input.AgentDefinitionId,
            ConversationId = input.ConversationId,
            MessageId = input.MessageId,
            ModelName = input.ModelName,
            ConfigHash = input.ConfigHash,
            LatencyMs = input.LatencyMs,
            PromptTokens = input.PromptTokens,
            CompletionTokens = input.CompletionTokens,
            Success = input.Success,
            ErrorClass = input.ErrorClass,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _ = _dbContext.AgentExecutionLogs.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(entity);
    }

    public async Task AddApprovalDecisionAsync(ApprovalDecisionAuditInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = new AgentExecutionLog
        {
            Id = Guid.NewGuid(),
            RecordKind = (int)AgentExecutionLogRecordKind.ApprovalDecision,
            SchemaVersion = 0,
            // Approval-decision rows are NOT per-agent telemetry: bind to Guid.Empty so they share one retention bucket
            // and never surface in the per-agent diagnostics view (which filters kind 0) or the run-envelope ledger
            // (which filters kind 1). Metadata only — every value below is a non-sensitive category label or id.
            AgentDefinitionId = Guid.Empty,
            InvocationId = input.InvocationId,
            // Reuse existing columns without a schema change: tool name → model_name, tool risk category → config_hash,
            // decision → terminal_status, source → provider. Success mirrors an approve for a coarse boolean read.
            ModelName = input.ToolName,
            ConfigHash = input.Category,
            Provider = input.Source,
            TerminalStatus = input.Decision,
            Success = string.Equals(input.Decision, ApprovalDecisions.Approve, StringComparison.Ordinal),
            LatencyMs = input.LatencyMs,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _ = _dbContext.AgentExecutionLogs.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddIntegrationInvocationAsync(IntegrationInvocationAuditInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        _ = _dbContext.AgentExecutionLogs.Add(BuildIntegrationInvocation(input, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The kind-3 row's whole shape, in one place. <see cref="IntegrationExecutionStore.TryTerminalizeAsync" />
    ///     builds it too — it adds the row to the SAME <c>SaveChanges</c> as the terminal status and the terminal
    ///     event, so a required audit row cannot be lost to a database failure after a committed terminal — and two
    ///     copies of this mapping would drift the moment either column moved.
    /// </summary>
    internal static AgentExecutionLog BuildIntegrationInvocation(IntegrationInvocationAuditInput input, long createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AgentExecutionLog
        {
            Id = Guid.NewGuid(),
            RecordKind = (int)AgentExecutionLogRecordKind.IntegrationInvocation,
            SchemaVersion = 0,
            // Same reason kind 2 binds Guid.Empty: an integration invocation is not per-agent telemetry, so it shares
            // one retention bucket and never surfaces in the per-agent diagnostics view (kind 0) or the run-envelope
            // ledger (kind 1).
            AgentDefinitionId = Guid.Empty,
            // ConversationId stays null even though every execution owns a conversation. ConversationFootprintPurge
            // deletes execution logs by conversation_id, so a null keeps a conversation purge from reaching the audit
            // row; these age out with the execution-log retention sweep instead. Every value on the row is a trigger
            // name, a key prefix, an id or a status, so nothing content-bearing survives that choice.
            ConversationId = null,
            InvocationId = input.InvocationId,
            RequestId = input.RequestId,
            // Reuse existing columns without a schema change: trigger name → model_name, requesting key prefix →
            // provider, target agent definition id → config_hash.
            ModelName = input.TriggerName,
            Provider = input.KeyPrefix,
            ConfigHash = input.TargetAgentDefinitionId.ToString("N"),
            TerminalStatus = input.TerminalStatus,
            TraceId = input.TraceId,
            Success = string.Equals(input.TerminalStatus, "completed", StringComparison.Ordinal),
            LatencyMs = input.LatencyMs,
            CreatedAtUtc = createdAtUtc
        };
    }

    public async Task<IReadOnlyList<AgentRunEnvelopeRecord>> ListRunEnvelopesAsync(Guid? conversationId, int limit, int offset = 0, CancellationToken cancellationToken = default)
    {
        // Floor the page bounds so a caller passing 0/negative still returns a sane (empty) page rather than throwing.
        var take = Math.Max(val1: 0, limit);
        var skip = Math.Max(val1: 0, offset);

        var query = _dbContext.AgentExecutionLogs
                              .AsNoTracking()
                              .Where(log => log.RecordKind == ChatRunEnvelopeKind);

        if (conversationId is { } id)
        {
            query = query.Where(log => log.ConversationId == id);
        }

        var entities = await query
                             .OrderByDescending(log => log.CreatedAtUtc)
                             .ThenByDescending(log => log.Id)
                             .Skip(skip)
                             .Take(take)
                             .ToListAsync(cancellationToken)
                             .ConfigureAwait(false);

        return entities.Select(ToEnvelopeRecord).ToArray();
    }

    public async Task<IReadOnlyList<AgentExecutionLogRecord>> ListByAgentAsync(Guid agentDefinitionId, int limit, int offset = 0, CancellationToken cancellationToken = default)
    {
        // Floor the page bounds so a caller passing 0/negative still returns a sane (empty) page rather than throwing.
        var take = Math.Max(val1: 0, limit);
        var skip = Math.Max(val1: 0, offset);

        var entities = await _dbContext.AgentExecutionLogs
                                       .AsNoTracking()
                                       // Diagnostics view is adaptive-memory rows only; run-envelope rows (kind 1) are a
                                       // separate ledger and must never surface here, regardless of their bound agent id.
                                       .Where(log => log.RecordKind == (int)AgentExecutionLogRecordKind.AdaptiveMemoryDiagnostics)
                                       .Where(log => log.AgentDefinitionId == agentDefinitionId)
                                       .OrderByDescending(log => log.CreatedAtUtc)
                                       .ThenByDescending(log => log.Id)
                                       .Skip(skip)
                                       .Take(take)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        return entities.Select(ToRecord).ToArray();
    }

    public async Task<int> DeleteOlderThanAsync(long cutoffEpochMs, CancellationToken cancellationToken = default)
    {
        return await _dbContext.AgentExecutionLogs
                               .Where(log => log.CreatedAtUtc < cutoffEpochMs)
                               .ExecuteDeleteAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TokenUsageAggregateRecord>> SummarizeTokenUsageAsync(long? fromEpochMsInclusive,
        long? toEpochMsExclusive,
        CancellationToken cancellationToken = default)
    {
        // Aggregate over the run-envelope ledger only (kind 1): those rows carry the full token set (prompt / completion
        // / reasoning / total). Memory-diagnostics rows (kind 0) are a separate producer with no reasoning/total and are
        // excluded. The group key is (model name, provider, UTC day). The day bucket is an integer division of the
        // unix-ms timestamp — SQLite translates it to a GROUP BY expression, so the whole aggregation runs set-based
        // server-side with no client-side materialization of individual rows. The per-provider rollup and grand totals
        // are folded from these buckets by the caller (the mapper), so no second query is issued.
        var query = _dbContext.AgentExecutionLogs
                              .AsNoTracking()
                              .Where(log => log.RecordKind == ChatRunEnvelopeKind);

        if (fromEpochMsInclusive is { } from)
        {
            query = query.Where(log => log.CreatedAtUtc >= from);
        }

        if (toEpochMsExclusive is { } to)
        {
            query = query.Where(log => log.CreatedAtUtc < to);
        }

        var buckets = await query
                            .GroupBy(log => new
                            {
                                log.ModelName,
                                log.Provider,
                                Day = log.CreatedAtUtc / MillisecondsPerDay
                            })
                            .Select(group => new
                            {
                                group.Key.ModelName,
                                group.Key.Provider,
                                group.Key.Day,
                                RunCount = group.Count(),
                                PromptTokens = group.Sum(log => (long)(log.PromptTokens ?? 0)),
                                CompletionTokens = group.Sum(log => (long)(log.CompletionTokens ?? 0)),
                                ReasoningTokens = group.Sum(log => (long)(log.ReasoningTokens ?? 0)),
                                TotalTokens = group.Sum(log => (long)(log.TotalTokens ?? 0))
                            })
                            .OrderByDescending(bucket => bucket.Day)
                            .ThenBy(bucket => bucket.Provider)
                            .ThenBy(bucket => bucket.ModelName)
                            .ToListAsync(cancellationToken)
                            .ConfigureAwait(false);

        return buckets
               .Select(bucket => new TokenUsageAggregateRecord(bucket.ModelName,
                   bucket.Provider,
                   bucket.Day * MillisecondsPerDay,
                   bucket.RunCount,
                   bucket.PromptTokens,
                   bucket.CompletionTokens,
                   bucket.ReasoningTokens,
                   bucket.TotalTokens))
               .ToArray();
    }

    public async Task<int> TrimToMaxPerAgentAsync(int maxPerAgent, CancellationToken cancellationToken = default)
    {
        if (maxPerAgent <= 0)
        {
            return 0;
        }

        // Set-based per-agent cap: a row is over the cap when at least maxPerAgent strictly-newer rows exist for the same
        // agent (ranked by CreatedAtUtc, newest kept). Counting newer rows lets the rank filter run as a single
        // ExecuteDeleteAsync without client-side materialization. Ties on CreatedAtUtc are kept together (never split by a
        // Guid comparison, which SQLite cannot translate) — at unix-ms granularity retention exactness on a tie is moot.
        return await _dbContext.AgentExecutionLogs
                               .Where(log => _dbContext.AgentExecutionLogs.Count(newer =>
                                                 newer.AgentDefinitionId == log.AgentDefinitionId
                                                 && newer.CreatedAtUtc > log.CreatedAtUtc)
                                             >= maxPerAgent)
                               .ExecuteDeleteAsync(cancellationToken)
                               .ConfigureAwait(false);
    }

    private static AgentExecutionLogRecord ToRecord(AgentExecutionLog entity)
    {
        return new AgentExecutionLogRecord(entity.Id,
            entity.AgentDefinitionId,
            entity.ConversationId,
            entity.MessageId,
            entity.ModelName,
            entity.ConfigHash,
            entity.LatencyMs,
            entity.PromptTokens,
            entity.CompletionTokens,
            entity.Success,
            entity.ErrorClass,
            entity.CreatedAtUtc);
    }

    private static AgentRunEnvelopeRecord ToEnvelopeRecord(AgentExecutionLog entity)
    {
        return new AgentRunEnvelopeRecord(entity.Id,
            entity.SchemaVersion,
            entity.AgentDefinitionId,
            entity.ConversationId,
            entity.MessageId,
            entity.InvocationId,
            entity.RequestId,
            entity.ModelName,
            entity.Provider,
            entity.TerminalStatus ?? string.Empty,
            entity.Success,
            entity.ErrorClass,
            entity.LatencyMs,
            entity.PromptTokens,
            entity.CompletionTokens,
            entity.ReasoningTokens,
            entity.TotalTokens,
            entity.ContentChunkCount,
            entity.ReasoningChunkCount,
            entity.TraceId,
            entity.StartedAtUtc,
            entity.CreatedAtUtc,
            entity.ToolSchemaTokens,
            entity.MaxToolSchemaTokens);
    }
}
