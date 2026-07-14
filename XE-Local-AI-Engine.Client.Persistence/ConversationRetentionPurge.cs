namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
///     Deletes a single conversation's complete footprint for the retention sweeper, but only after re-confirming, inside
///     the deletion transaction, that the conversation is still eligible. Candidate ids are selected without a lock, so a
///     send or touch can make a candidate active again between selection and deletion; the caller runs this under the
///     conversation's exclusive write lock (<c>NodeChatPersistenceWriter</c>) and the re-check here is the second guard —
///     together they stop retention from deleting a just-touched conversation (or letting a send strand an orphan upload
///     after a blind delete).
/// </summary>
public static class ConversationRetentionPurge
{
    /// <summary>
    ///     Re-checks the retention-eligibility predicate (<c>purged</c> or <c>last_seen_utc &lt;= cutoff</c>) for
    ///     <paramref name="conversationId" /> on <paramref name="dbContext" />'s connection and, only if it still holds,
    ///     deletes the conversation's complete DB footprint via <see cref="ConversationFootprintPurge" /> in a single
    ///     transaction. Returns <see langword="true" /> when the conversation was deleted, <see langword="false" /> when
    ///     it was no longer eligible (touched after selection, or already gone). The caller must hold the conversation's
    ///     exclusive write lock and owns any on-disk upload-blob teardown for a returned <see langword="true" />.
    /// </summary>
    public static async Task<bool> TryPurgeIfExpiredAsync(NodeChatDbContext dbContext, Guid conversationId, long cutoffUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Re-evaluate eligibility against the committed row inside the transaction. The predicate mirrors the candidate
        // selection exactly (soft-purged OR aged past the cutoff); a row touched after selection now carries a newer
        // last_seen_utc and fails it, and a row already deleted returns no rows and also fails it.
        var stillEligible = await dbContext.Database
                                           .SqlQueryRaw<Guid>(
                                               "SELECT conversation_id FROM conversations WHERE conversation_id = {0} AND (purged <> 0 OR last_seen_utc <= {1})",
                                               conversationId,
                                               cutoffUtc)
                                           .AnyAsync(cancellationToken)
                                           .ConfigureAwait(false);
        if (!stillEligible)
        {
            return false;
        }

        await ConversationFootprintPurge.DeleteAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
