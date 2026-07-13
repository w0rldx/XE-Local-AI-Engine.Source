namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for node retention data.
/// </summary>
public sealed class NodeRetentionStore(NodeChatDbContext dbContext) : INodeRetentionStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<Guid>> SweepExpiredConversationsAsync(long cutoffUtc, CancellationToken cancellationToken = default)
    {
        var candidateConversationIds = await _dbContext.Conversations
                                                       .Where(conversation => conversation.Purged || conversation.LastSeenUtc <= cutoffUtc)
                                                       .Select(conversation => conversation.ConversationId)
                                                       .ToListAsync(cancellationToken)
                                                       .ConfigureAwait(false);

        if (candidateConversationIds.Count == 0)
        {
            return [];
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Delete each conversation's complete DB footprint through the shared helper so retention deletes exactly what
        // the interactive immediate-purge path deletes (feedback + uploaded-file rows included), not just messages.
        foreach (var conversationId in candidateConversationIds)
        {
            await ConversationFootprintPurge.DeleteAsync(_dbContext, conversationId, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // The ids are returned so the caller can tear down each conversation's on-disk upload blobs after the DB commit.
        return candidateConversationIds;
    }
}
