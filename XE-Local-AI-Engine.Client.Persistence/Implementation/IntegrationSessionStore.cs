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
