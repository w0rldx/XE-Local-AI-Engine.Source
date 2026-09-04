namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     EF persistence boundary for integration sessions. There is no create path here on purpose:
///     <see cref="IntegrationExecutionStore.AcceptAsync" /> inserts the session inside the admission transaction, so a
///     second insert path would be a second admission gate.
/// </summary>
public sealed class IntegrationSessionStore(NodeChatDbContext dbContext) : IIntegrationSessionStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IntegrationSessionSnapshot?> GetByIdAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.IntegrationSessions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == sessionId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IntegrationSessionSnapshot?> GetForPrincipalAsync(Guid sessionId, Guid principalId, CancellationToken cancellationToken = default)
    {
        // Both columns in ONE predicate rather than a load followed by a comparison: a foreign session and a missing
        // one have to be the same non-result, and a shape that returns the row first invites a caller to look at it.
        var entity = await _dbContext.IntegrationSessions.AsNoTracking()
                                     .SingleOrDefaultAsync(row => row.Id == sessionId && row.PrincipalId == principalId, cancellationToken)
                                     .ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IntegrationSessionSnapshot?> FindByConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.IntegrationSessions.AsNoTracking()
                                     .SingleOrDefaultAsync(row => row.ConversationId == conversationId, cancellationToken)
                                     .ConfigureAwait(false);
        return entity is null ? null : ToSnapshot(entity);
    }

    public async Task<IReadOnlyList<IntegrationSessionSnapshot>> ListAsync(Guid? triggerId,
        IntegrationSessionStatus? status,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        // Ordered BEFORE Skip/Take, and by two columns: LastActivityUtc is a millisecond stamp, so the id tie-break is
        // what keeps two sessions touched in the same millisecond from paging non-deterministically.
        var entities = await Matching(triggerId, status)
                             .OrderByDescending(row => row.LastActivityUtc)
                             .ThenByDescending(row => row.Id)
                             .Skip(Math.Max(val1: 0, offset))
                             .Take(Math.Max(val1: 0, limit))
                             .ToListAsync(cancellationToken)
                             .ConfigureAwait(false);
        return [.. entities.Select(ToSnapshot)];
    }

    public Task<int> CountAsync(Guid? triggerId, IntegrationSessionStatus? status, CancellationToken cancellationToken = default) =>
        // No Skip/Take: the total a pager labels its window with is the whole matching set, not the window.
        Matching(triggerId, status).CountAsync(cancellationToken);

    /// <summary>
    ///     The filter half of both reads, in ONE place, so a count can never disagree with the page it labels about
    ///     which rows are in scope.
    /// </summary>
    private IQueryable<IntegrationSession> Matching(Guid? triggerId, IntegrationSessionStatus? status)
    {
        var query = _dbContext.IntegrationSessions.AsNoTracking();

        if (triggerId is { } trigger)
        {
            query = query.Where(row => row.TriggerId == trigger);
        }

        if (status is { } sessionStatus)
        {
            query = query.Where(row => row.Status == sessionStatus);
        }

        return query;
    }

    /// <summary>
    ///     The backstop half of an operator delete. The mechanism is the conversation purge, which already cascades
    ///     this row away; this removes it when that purge could not run, so the operator's list never keeps a session
    ///     whose conversation is gone.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var deleted = await _dbContext.IntegrationSessions.Where(row => row.Id == sessionId)
                                      .ExecuteDeleteAsync(cancellationToken)
                                      .ConfigureAwait(false);
        return deleted > 0;
    }

    public async Task<bool> CloseAsync(Guid sessionId, long atUtc, CancellationToken cancellationToken = default)
    {
        // Idempotent: closing an already-closed session matches the row and rewrites the same status, so a repeated
        // close is a success rather than a 404.
        var updated = await _dbContext.IntegrationSessions.Where(row => row.Id == sessionId)
                                      .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, IntegrationSessionStatus.Closed)
                                                                            .SetProperty(row => row.LastActivityUtc, atUtc),
                                          cancellationToken)
                                      .ConfigureAwait(false);
        return updated > 0;
    }

    internal static IntegrationSessionSnapshot ToSnapshot(IntegrationSession entity) =>
        new(entity.Id,
            entity.TriggerId,
            entity.PrincipalId,
            entity.ConversationId,
            entity.AgentDefinitionId,
            entity.Status,
            entity.CreatedAtUtc,
            entity.LastActivityUtc,
            entity.ExecutionCount,
            entity.LastSequence);
}
