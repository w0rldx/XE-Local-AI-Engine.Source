namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Read-only conversation queries behind <see cref="NodeChatPersistenceService" />: the conversation list and the
///     full conversation-with-messages load. Shares the single <see cref="NodeChatPersistenceWriter" /> so reads
///     serialize against in-flight writes on the same write key.
/// </summary>
internal sealed class NodeChatReadModel(NodeChatPersistenceWriter writer)
{
    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.IncludeArchived
            ? await ListAllConversationsAsync(request, cancellationToken).ConfigureAwait(false)
            : await ListActiveConversationsAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatConversationDto?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(conversationId),
            async (dbContext, token) =>
            {
                await using var conversationCommand = dbContext.Database.GetDbConnection().CreateCommand();
                conversationCommand.CommandText = """
                                                  SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, selected_path_json, agent_definition_id, memory_excluded
                                                  FROM conversations
                                                  WHERE conversation_id = $conversation_id AND purged = 0;
                                                  """;
                AddParameter(conversationCommand, "$conversation_id", conversationId);

                await OpenIfNeededAsync(conversationCommand.Connection, token).ConfigureAwait(false);
                await using var conversationReader = await conversationCommand.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await conversationReader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                // Title is stored as an encrypted BLOB; read raw bytes and decrypt via the db-context gateway
                // (mirrors ReadConversationSummariesAsync in NodeChatPersistenceSql).
                var titleBytes = await conversationReader.IsDBNullAsync(ordinal: 1, token).ConfigureAwait(false)
                    ? null
                    : await conversationReader.GetFieldValueAsync<byte[]>(ordinal: 1, token).ConfigureAwait(false);

                var dto = new NodeChatConversationDto(Guid.Parse(conversationReader.GetString(0)),
                    DecryptTitle(titleBytes, dbContext, conversationId),
                    await conversationReader.IsDBNullAsync(ordinal: 2, token).ConfigureAwait(false) ? null : conversationReader.GetString(2),
                    conversationReader.GetInt64(3),
                    conversationReader.GetInt64(4),
                    conversationReader.GetBoolean(5),
                    await ReadMessagesAsync(dbContext, conversationId, token).ConfigureAwait(false),
                    conversationReader.GetString(6),
                    conversationReader.GetBoolean(7),
                    conversationReader.GetBoolean(8),
                    await conversationReader.IsDBNullAsync(ordinal: 9, token).ConfigureAwait(false) ? null : Guid.Parse(conversationReader.GetString(9)),
                    DeserializeSelectedPath(await conversationReader.IsDBNullAsync(ordinal: 10, token).ConfigureAwait(false) ? null : conversationReader.GetString(10)),
                    await conversationReader.IsDBNullAsync(ordinal: 11, token).ConfigureAwait(false) ? null : Guid.Parse(conversationReader.GetString(11)),
                    conversationReader.GetBoolean(12));

                return dto;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListActiveConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(Guid.Empty),
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      SELECT c.conversation_id, c.title, c.created_at_utc, c.last_seen_utc, c.purged,
                                             m.content, m.status, c.origin, c.is_pinned, c.archived
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      WHERE c.purged = 0 AND c.archived = 0
                                      ORDER BY c.is_pinned DESC, c.last_seen_utc DESC
                                      LIMIT $limit;
                                      """;
                return await ReadConversationSummariesAsync(command, dbContext, request.Limit, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListAllConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(Guid.Empty),
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      SELECT c.conversation_id, c.title, c.created_at_utc, c.last_seen_utc, c.purged,
                                             m.content, m.status, c.origin, c.is_pinned, c.archived
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      WHERE c.purged = 0
                                      ORDER BY c.is_pinned DESC, c.last_seen_utc DESC
                                      LIMIT $limit;
                                      """;
                return await ReadConversationSummariesAsync(command, dbContext, request.Limit, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
