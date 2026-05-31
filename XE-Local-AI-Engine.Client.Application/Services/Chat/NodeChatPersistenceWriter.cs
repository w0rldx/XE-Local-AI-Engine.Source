namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Represents node chat persistence writer.
/// </summary>
public sealed class NodeChatPersistenceWriter(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<NodeChatPersistenceWriteKey, SemaphoreSlim> _locks = new();
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task ExecuteAsync(NodeChatPersistenceWriteKey key,
        Func<NodeChatDbContext, CancellationToken, Task> persistenceOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistenceOperation);

        await ExecuteAsync(key,
            async (dbContext, token) =>
            {
                await persistenceOperation(dbContext, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> ExecuteAsync<TResult>(NodeChatPersistenceWriteKey key,
        Func<NodeChatDbContext, CancellationToken, Task<TResult>> persistenceOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistenceOperation);

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            return await persistenceOperation(dbContext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>
///     Value object carrying struct data.
/// </summary>
public readonly record struct NodeChatPersistenceWriteKey(Guid ConversationId, Guid? MessageId)
{
    public static NodeChatPersistenceWriteKey ForConversation(Guid conversationId)
    {
        return new NodeChatPersistenceWriteKey(conversationId, null);
    }

    public static NodeChatPersistenceWriteKey ForMessage(Guid conversationId, Guid messageId)
    {
        return new NodeChatPersistenceWriteKey(conversationId, messageId);
    }
}
