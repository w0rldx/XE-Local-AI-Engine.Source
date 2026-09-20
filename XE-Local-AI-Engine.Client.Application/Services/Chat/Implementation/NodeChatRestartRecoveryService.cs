namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class NodeChatRestartRecoveryService
{
    public const string RestartInterruptedError = "Interrupted by application restart before terminal status.";

    private const string AssistantRole = "assistant";

    private readonly NodeChatPersistenceWriter _writer;

    public NodeChatRestartRecoveryService(NodeChatPersistenceWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public async Task<int> RecoverInterruptedMessagesAsync(long recoveredAtUtc, CancellationToken cancellationToken = default)
    {
        // Startup-only reconciliation across every conversation; runs before the app serves traffic. Uses the shared
        // Guid.Empty gate exclusively, mirroring the list read model that keys global queries on the same id.
        return await _writer.ExecuteConversationExclusiveAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

                // Terminalize every non-terminal assistant row whatever its Origin, since a restart orphans local and
                // mirrored rows alike. A unit test pins this status literal to NodeChatMessageTransitions.RecoverySources.
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
                    token);

                // Durable run-envelope reconcile: a crash after a terminal commit can leave a row envelope-less, so one
                // is backfilled FROM the persisted row, in this transaction, idempotently through the NOT EXISTS guard.
                _ = await dbContext.Database.ExecuteSqlRawAsync(sql: """
                                                                     INSERT INTO agent_execution_logs
                                                                         (id, record_kind, schema_version, agent_definition_id, conversation_id, message_id, request_id,
                                                                          model_name, config_hash, terminal_status, latency_ms, success, created_at_utc)
                                                                     SELECT
                                                                         m.message_id, {0}, {1}, COALESCE(m.agent_definition_id, {2}), m.conversation_id, m.message_id, m.request_id,
                                                                         '', '', m.status, 0, CASE WHEN m.status = {3} THEN 1 ELSE 0 END, {4}
                                                                     FROM messages m
                                                                     WHERE m.role = {5}
                                                                       AND m.status IN ({3}, {6}, {7}, {8})
                                                                       AND NOT EXISTS (
                                                                           SELECT 1 FROM agent_execution_logs e
                                                                           WHERE e.record_kind = {0} AND e.message_id = m.message_id);
                                                                     """,
                    [
                        (int)AgentExecutionLogRecordKind.ChatRunEnvelope,
                        AgentRunEnvelope.CurrentSchemaVersion,
                        Guid.Empty,
                        NodeChatMessageStatusValues.Completed,
                        recoveredAtUtc,
                        AssistantRole,
                        NodeChatMessageStatusValues.Failed,
                        NodeChatMessageStatusValues.Cancelled,
                        NodeChatMessageStatusValues.Interrupted
                    ],
                    token);

                await transaction.CommitAsync(token);
                return recoveredCount;
            },
            cancellationToken);
    }
}
