namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for node retention data.
/// </summary>
public sealed class NodeRetentionStore(NodeChatDbContext dbContext) : INodeRetentionStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<IReadOnlyList<Guid>> ListExpiredConversationCandidatesAsync(long cutoffUtc, CancellationToken cancellationToken = default)
    {
        // Read-only candidate selection: no lock and no delete here. Each candidate is re-checked and deleted under the
        // conversation's exclusive write lock by the caller (see ConversationRetentionPurge), because a send or touch can
        // make a candidate active again between this selection and its deletion.
        return await _dbContext.Conversations
                               .Where(conversation => conversation.Purged || conversation.LastSeenUtc <= cutoffUtc)
                               .Select(conversation => conversation.ConversationId)
                               .ToListAsync(cancellationToken)
                               .ConfigureAwait(false);
    }
}
