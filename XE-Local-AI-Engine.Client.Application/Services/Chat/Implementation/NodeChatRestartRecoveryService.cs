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

                // Durable run-envelope reconcile (R4 round 3): a crash or an envelope-write failure after ANY terminal
                // message commit — completed, failed, cancelled, or interrupted — can leave that assistant row without an
                // envelope. Backfill one envelope per terminal assistant message that lacks one, in the SAME transaction,
                // deriving the terminal status and success FROM the persisted message row (so the envelope matches the row's
                // actual outcome), the bound agent id from the row, and a deterministic id = message_id. Because it selects
                // FROM messages it can never orphan (a purged message has no row to select), and the NOT EXISTS guard plus
                // the filtered unique index keep it idempotent (already-enveloped rows are skipped, re-runs never
                // duplicate). Tokens / duration / model are unknown at reconcile and left empty — the row records the
                // terminal lifecycle, not the (lost) generation detail.
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
                    token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return recoveredCount;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
