namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for node retention data.
/// </summary>
public interface INodeRetentionStore
{
    /// <summary>
    ///     Returns the ids of conversations that are candidates for retention deletion: soft-purged, or whose
    ///     <c>last_seen_utc</c> is at or before <paramref name="cutoffUtc" />. This is a lock-free read; the caller must
    ///     re-check eligibility and delete each candidate under the conversation's exclusive write lock (see
    ///     <c>ConversationRetentionPurge</c>), so a candidate may legitimately survive if it is touched between selection
    ///     and deletion.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListExpiredConversationCandidatesAsync(long cutoffUtc, CancellationToken cancellationToken = default);
}
