namespace XE_Local_AI_Engine.Client.Services.Chat;

using Microsoft.EntityFrameworkCore;

public sealed class NodeChatRestartRecoveryService(NodeChatPersistenceWriter writer)
{
    public const string RestartInterruptedError = "Interrupted by application restart before terminal status.";

    private const string AssistantRole = "assistant";

    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<int> RecoverInterruptedMessagesAsync(long recoveredAtUtc, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteAsync(
            NodeChatPersistenceWriteKey.ForConversation(Guid.Empty),
            async (dbContext, token) =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                var recoveredCount = await dbContext.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE messages
                    SET status = {0},
                        updated_at_utc = {1},
                        error = {2}
                    WHERE role = {3}
                      AND status IN ({4}, {5});
                    """,
                    [
                        NodeChatMessageStatusValues.Interrupted,
                        recoveredAtUtc,
                        RestartInterruptedError,
                        AssistantRole,
                        NodeChatMessageStatusValues.Pending,
                        NodeChatMessageStatusValues.Streaming
                    ],
                    token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return recoveredCount;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
