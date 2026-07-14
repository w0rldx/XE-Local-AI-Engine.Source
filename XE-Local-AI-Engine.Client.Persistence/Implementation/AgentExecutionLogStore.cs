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
    // Current run-envelope shape version. Bump when the envelope's field set changes so a reader can tell old rows apart.
    private const int CurrentRunEnvelopeSchemaVersion = 1;

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

    public async Task AddRunEnvelopeAsync(AgentRunEnvelopeInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var entity = new AgentExecutionLog
        {
            Id = Guid.NewGuid(),
            RecordKind = (int)AgentExecutionLogRecordKind.ChatRunEnvelope,
            SchemaVersion = CurrentRunEnvelopeSchemaVersion,
            // The bound agent id is not available at the terminalization seam (it lives only in the message metadata
            // blob); recorded as Guid.Empty so every envelope row shares one retention bucket and never collides with a
            // real agent's diagnostics view.
            AgentDefinitionId = Guid.Empty,
            ConversationId = input.ConversationId,
            MessageId = input.MessageId,
            InvocationId = input.InvocationId,
            RequestId = input.RequestId,
            ModelName = input.ModelName,
            ConfigHash = string.Empty,
            TerminalStatus = input.TerminalStatus,
            LatencyMs = input.DurationMs,
            PromptTokens = input.PromptTokens,
            CompletionTokens = input.CompletionTokens,
            ContentChunkCount = input.ContentChunkCount,
            ReasoningChunkCount = input.ReasoningChunkCount,
            TraceId = input.TraceId,
            Success = input.Success,
            // Reuses the ErrorClass column for the failure-category enum name (text-free, same redaction discipline).
            ErrorClass = input.FailureCategory,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        _ = _dbContext.AgentExecutionLogs.Add(entity);
        _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AgentExecutionLogRecord>> ListByAgentAsync(Guid agentDefinitionId, int limit, int offset = 0, CancellationToken cancellationToken = default)
    {
        // Floor the page bounds so a caller passing 0/negative still returns a sane (empty) page rather than throwing.
        var take = Math.Max(val1: 0, limit);
        var skip = Math.Max(val1: 0, offset);

        var entities = await _dbContext.AgentExecutionLogs
                                       .AsNoTracking()
                                       // Diagnostics view is adaptive-memory rows only; run-envelope rows (kind 1, always
                                       // Guid.Empty agent) are a separate ledger and must never surface here.
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
}
