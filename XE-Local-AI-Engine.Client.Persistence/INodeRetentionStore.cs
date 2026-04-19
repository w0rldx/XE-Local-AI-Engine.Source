namespace XE_Local_AI_Engine.Client.Persistence;

public interface INodeRetentionStore
{
    Task<int> SweepExpiredConversationsAsync(long cutoffUtc, CancellationToken cancellationToken = default);
}
