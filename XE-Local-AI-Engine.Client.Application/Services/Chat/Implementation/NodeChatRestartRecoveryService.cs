namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class NodeChatRestartRecoveryService
{
    public const string RestartInterruptedError = "Interrupted by application restart before terminal status.";

    private const string AssistantRole = "assistant";
    private const long MillisecondsPerDay = 86_400_000L;

    private readonly INodeSettingsStore _settingsStore;
    private readonly Lazy<IUsageProviderResolver> _usageProviderResolver;
    private readonly NodeChatPersistenceWriter _writer;

    public NodeChatRestartRecoveryService(NodeChatPersistenceWriter writer, INodeSettingsStore settingsStore, Lazy<IUsageProviderResolver> usageProviderResolver)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(usageProviderResolver);
        _writer = writer;
        _settingsStore = settingsStore;
        _usageProviderResolver = usageProviderResolver;
    }

    public async Task<int> RecoverInterruptedMessagesAsync(long recoveredAtUtc, CancellationToken cancellationToken = default)
    {
        // The envelope backfill reaches back only as far as execution-log retention keeps envelopes; an older row's envelope was swept on purpose.
        // One settings read covers both values.
        var settings = await _settingsStore.LoadAsync(cancellationToken);
        var backfillSinceUtc = settings.AgentExecutionLogRetentionEnabled ?? StoredNodeSettings.DefaultAgentExecutionLogRetentionEnabled
            ? recoveredAtUtc - ((settings.AgentExecutionLogRetentionDays ?? StoredNodeSettings.DefaultAgentExecutionLogRetentionDays) * MillisecondsPerDay)
            : long.MinValue;

        // Startup-only reconciliation across every conversation; runs before the app serves traffic. Uses the shared
        // Guid.Empty gate exclusively, mirroring the list read model that keys global queries on the same id.
        return await _writer.ExecuteConversationExclusiveAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                Dictionary<string, string> providers = new(StringComparer.Ordinal);
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

                // Durable run-envelope reconcile: a crash after a terminal commit can leave a row envelope-less, so one is
                // backfilled FROM the persisted row, in this transaction. The model sits in the encrypted metadata, so it is read here, not in SQL.
                foreach (var row in await ReadEnvelopelessRowsAsync(dbContext, transaction.GetDbTransaction(), backfillSinceUtc, token))
                {
                    if (!providers.TryGetValue(row.Model ?? string.Empty, out var provider))
                    {
                        provider = await _usageProviderResolver.Value.ResolveAsync(row.Model, token);
                        providers[row.Model ?? string.Empty] = provider;
                    }

                    await NodeChatMessageCommands.WriteRunEnvelopeRowAsync(dbContext,
                        transaction.GetDbTransaction(),
                        row.ConversationId,
                        row.MessageId,
                        row.RequestId,
                        row.AgentDefinitionId,
                        row.Status,
                        row.Model,
                        promptTokens: null,
                        completionTokens: null,
                        reasoningTokens: null,
                        totalTokens: null,
                        new AgentRunEnvelopeMetadata
                        {
                            InvocationId = null,
                            DurationMs = 0L
                        },
                        provider,
                        recoveredAtUtc,
                        NodeChatMessageCommands.RunEnvelopeWriteMode.InsertIfAbsent,
                        token);
                }

                await transaction.CommitAsync(token);
                return recoveredCount;
            },
            cancellationToken);
    }

    // Terminal assistant rows inside the retention window that have no run envelope yet, with the model decrypted from their metadata.
    private static async Task<IReadOnlyList<EnvelopelessRow>> ReadEnvelopelessRowsAsync(NodeChatDbContext dbContext,
        DbTransaction transaction,
        long backfillSinceUtc,
        CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT m.message_id, m.conversation_id, m.request_id, m.agent_definition_id, m.status, m.metadata_json
                              FROM messages m
                              WHERE m.role = $role
                                AND m.status IN ($completed, $failed, $cancelled, $interrupted)
                                AND m.updated_at_utc >= $since
                                AND NOT EXISTS (
                                    SELECT 1 FROM agent_execution_logs e
                                    WHERE e.record_kind = $record_kind AND e.message_id = m.message_id);
                              """;
        NodeChatPersistenceSql.AddParameter(command, "$role", AssistantRole);
        NodeChatPersistenceSql.AddParameter(command, "$completed", NodeChatMessageStatusValues.Completed);
        NodeChatPersistenceSql.AddParameter(command, "$failed", NodeChatMessageStatusValues.Failed);
        NodeChatPersistenceSql.AddParameter(command, "$cancelled", NodeChatMessageStatusValues.Cancelled);
        NodeChatPersistenceSql.AddParameter(command, "$interrupted", NodeChatMessageStatusValues.Interrupted);
        NodeChatPersistenceSql.AddParameter(command, "$since", backfillSinceUtc);
        NodeChatPersistenceSql.AddParameter(command, "$record_kind", (int)AgentExecutionLogRecordKind.ChatRunEnvelope);
        await NodeChatPersistenceSql.OpenIfNeededAsync(command.Connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<EnvelopelessRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = Guid.Parse(reader.GetString(0));
            var conversationId = Guid.Parse(reader.GetString(1));
            var metadataJson = await reader.IsDBNullAsync(ordinal: 5, cancellationToken)
                ? null
                : dbContext.DecryptMessageMetadata(await reader.GetFieldValueAsync<byte[]>(ordinal: 5, cancellationToken), conversationId, messageId);
            rows.Add(new EnvelopelessRow
            {
                MessageId = messageId,
                ConversationId = conversationId,
                RequestId = await reader.IsDBNullAsync(ordinal: 2, cancellationToken) ? null : Guid.Parse(reader.GetString(2)),
                AgentDefinitionId = await reader.IsDBNullAsync(ordinal: 3, cancellationToken) ? null : Guid.Parse(reader.GetString(3)),
                Status = reader.GetString(4),
                Model = NodeChatMetadataSerializer.DeserializeMetadata(metadataJson).Model
            });
        }

        return rows;
    }

    private sealed class EnvelopelessRow
    {
        public required Guid MessageId { get; init; }

        public required Guid ConversationId { get; init; }

        public required Guid? RequestId { get; init; }

        public required Guid? AgentDefinitionId { get; init; }

        public required string Status { get; init; }

        public required string? Model { get; init; }
    }
}
