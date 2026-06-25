namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for i node retention data.
/// </summary>
public interface INodeRetentionStore
{
    Task<int> SweepExpiredConversationsAsync(long cutoffUtc, CancellationToken cancellationToken = default);
}
