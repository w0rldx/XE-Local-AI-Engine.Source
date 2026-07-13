namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for i node retention data.
/// </summary>
public interface INodeRetentionStore
{
    /// <summary>
    ///     Deletes the complete DB footprint of every conversation that is soft-purged or whose <c>last_seen_utc</c> is
    ///     at or before <paramref name="cutoffUtc" />, in a single transaction. Returns the ids that were deleted so the
    ///     caller can tear down their on-disk upload blobs after the commit.
    /// </summary>
    Task<IReadOnlyList<Guid>> SweepExpiredConversationsAsync(long cutoffUtc, CancellationToken cancellationToken = default);
}
