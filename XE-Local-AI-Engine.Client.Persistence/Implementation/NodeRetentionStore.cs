namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;

public sealed class NodeRetentionStore(NodeChatDbContext dbContext) : INodeRetentionStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<int> SweepExpiredConversationsAsync(long cutoffUtc, CancellationToken cancellationToken = default)
    {
        var candidateConversationIds = await _dbContext.Conversations
                                                       .Where(conversation => conversation.Purged || conversation.LastSeenUtc <= cutoffUtc)
                                                       .Select(conversation => conversation.ConversationId)
                                                       .ToListAsync(cancellationToken)
                                                       .ConfigureAwait(false);

        if (candidateConversationIds.Count == 0)
        {
            return 0;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        _ = await _dbContext.Messages
                            .Where(message => candidateConversationIds.Contains(message.ConversationId))
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);

        _ = await _dbContext.ToolEvents
                            .Where(toolEvent => candidateConversationIds.Contains(toolEvent.ConversationId))
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);

        _ = await _dbContext.PurgedTombstones
                            .Where(tombstone => candidateConversationIds.Contains(tombstone.ConversationId))
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);

        var deletedConversationCount = await _dbContext.Conversations
                                                       .Where(conversation => candidateConversationIds.Contains(conversation.ConversationId))
                                                       .ExecuteDeleteAsync(cancellationToken)
                                                       .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return deletedConversationCount;
    }
}
