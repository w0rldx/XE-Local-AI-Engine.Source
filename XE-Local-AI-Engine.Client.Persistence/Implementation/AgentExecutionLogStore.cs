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

        // Idempotent on the assistant message id: exactly one envelope per terminalized turn. A retry or a crash-recovery
        // backfill for a message that already has an envelope is a no-op (first write wins), mirroring the scheduler's
        // fire-instance upsert. When no message id is available (should not occur at the terminalization seam) the row is
        // inserted unconditionally — there is no key to dedupe on.
        if (input.MessageId is { } messageId
            && await FindEnvelopeByMessageAsync(messageId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        var entity = BuildEnvelopeEntity(input);
        _ = _dbContext.AgentExecutionLogs.Add(entity);

        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (input.MessageId is { } retryMessageId)
        {
            // A concurrent writer won the race on the same message id (the filtered unique index rejected this insert).
            // Detach our losing entity and confirm the winner exists so the upsert stays idempotent; only rethrow if the
            // failure was not the expected uniqueness collision (winner truly absent).
            _dbContext.Entry(entity).State = EntityState.Detached;

            if (await FindEnvelopeByMessageAsync(retryMessageId, cancellationToken).ConfigureAwait(false) is null)
            {
                throw;
            }
        }
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

    private Task<AgentExecutionLog?> FindEnvelopeByMessageAsync(Guid messageId, CancellationToken cancellationToken)
    {
        return _dbContext.AgentExecutionLogs
                         .AsNoTracking()
                         .FirstOrDefaultAsync(log => log.RecordKind == ChatRunEnvelopeKind && log.MessageId == messageId, cancellationToken);
    }

    private AgentExecutionLog BuildEnvelopeEntity(AgentRunEnvelopeInput input)
    {
        return new AgentExecutionLog
        {
            Id = Guid.NewGuid(),
            RecordKind = ChatRunEnvelopeKind,
            SchemaVersion = AgentRunEnvelope.CurrentSchemaVersion,
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
            ReasoningTokens = input.ReasoningTokens,
            TotalTokens = input.TotalTokens,
            ContentChunkCount = input.ContentChunkCount,
            ReasoningChunkCount = input.ReasoningChunkCount,
            TraceId = input.TraceId,
            StartedAtUtc = input.StartedAtUtc,
            Success = input.Success,
            // Reuses the ErrorClass column for the failure-category enum name (text-free, same redaction discipline).
            ErrorClass = input.FailureCategory,
            CreatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
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

    private static AgentRunEnvelopeRecord ToEnvelopeRecord(AgentExecutionLog entity)
    {
        return new AgentRunEnvelopeRecord(entity.Id,
            entity.SchemaVersion,
            entity.ConversationId,
            entity.MessageId,
            entity.InvocationId,
            entity.RequestId,
            entity.ModelName,
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
