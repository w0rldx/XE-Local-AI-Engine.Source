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
            entity.CreatedAtUtc);
    }
}
