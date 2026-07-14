namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

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
                // Origin=Remote platform mirrors): a restart orphans both the same way. The status list is the
                // recovery source set from NodeChatMessageTransitions.RecoverySources — pending, queued (held before
                // the collision lease is acquired), and streaming — so a crash mid-queue does not leave a row dangling
                // forever, and a terminal row (including a user Cancelled) is never downgraded to Interrupted. A unit
                // test pins this literal to that table set so the two cannot drift.
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

                // Durable run-envelope backfill (R4): a crash between the terminal message commit and the best-effort
                // envelope write leaves an interrupted assistant row with no envelope. Backfill one thin envelope per
                // interrupted assistant message that lacks one, in the SAME transaction, deriving the correlation ids
                // (and a deterministic id = message_id) from the message row. The NOT EXISTS guard plus the filtered
                // unique index keep it idempotent: an already-enveloped message is skipped, so re-running never
                // duplicates. Tokens/duration/model are unknown at recovery and left empty — the row records the
                // interrupted lifecycle, not the (lost) generation detail.
                _ = await dbContext.Database.ExecuteSqlRawAsync(sql: """
                                                                    INSERT INTO agent_execution_logs
                                                                        (id, record_kind, schema_version, agent_definition_id, conversation_id, message_id, request_id,
                                                                         model_name, config_hash, terminal_status, latency_ms, success, created_at_utc)
                                                                    SELECT
                                                                        m.message_id, {0}, {1}, {2}, m.conversation_id, m.message_id, m.request_id,
                                                                        '', '', {3}, 0, 0, {4}
                                                                    FROM messages m
                                                                    WHERE m.role = {5}
                                                                      AND m.status = {3}
                                                                      AND NOT EXISTS (
                                                                          SELECT 1 FROM agent_execution_logs e
                                                                          WHERE e.record_kind = {0} AND e.message_id = m.message_id);
                                                                    """,
                    [
                        (int)AgentExecutionLogRecordKind.ChatRunEnvelope,
                        AgentRunEnvelope.CurrentSchemaVersion,
                        Guid.Empty,
                        NodeChatMessageStatusValues.Interrupted,
                        recoveredAtUtc,
                        AssistantRole
                    ],
                    token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return recoveredCount;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
