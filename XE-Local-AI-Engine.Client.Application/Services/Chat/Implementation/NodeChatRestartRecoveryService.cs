namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;

public sealed class NodeChatRestartRecoveryService(NodeChatPersistenceWriter writer)
{
    public const string RestartInterruptedError = "Interrupted by application restart before terminal status.";

    private const string AssistantRole = "assistant";

    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<int> RecoverInterruptedMessagesAsync(long recoveredAtUtc, CancellationToken cancellationToken = default)
    {
        // Startup-only reconciliation across every conversation; runs before the app serves traffic. Uses the shared
        // Guid.Empty gate exclusively, mirroring the list read model that keys global queries on the same id.
        return await _writer.ExecuteConversationExclusiveAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                // Terminalize every non-terminal assistant row regardless of Origin (Local loopback AND
                // Origin=Remote platform mirrors): a restart orphans both the same way. The status list must
                // cover the full non-terminal set — pending, queued (held before the collision lease is
                // acquired), and streaming — so a crash mid-queue does not leave a row dangling forever.
                var recoveredCount = await dbContext.Database.ExecuteSqlRawAsync(sql: """
                                                                                      UPDATE messages
                                                                                      SET status = {0},
                                                                                          updated_at_utc = {1},
                                                                                          error = {2}
                                                                                      WHERE role = {3}
                                                                                        AND status IN ({4}, {5}, {6});
                                                                                      """,
                    [
                        NodeChatMessageStatusValues.Interrupted,
                        recoveredAtUtc,
                        RestartInterruptedError,
                        AssistantRole,
                        NodeChatMessageStatusValues.Pending,
                        NodeChatMessageStatusValues.Queued,
                        NodeChatMessageStatusValues.Streaming
                    ],
                    token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return recoveredCount;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
