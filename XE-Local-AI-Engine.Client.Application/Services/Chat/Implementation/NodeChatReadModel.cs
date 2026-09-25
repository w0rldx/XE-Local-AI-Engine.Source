namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Read-only conversation queries behind <see cref="NodeChatPersistenceService" />: the conversation list and the
///     full conversation-with-messages load. Shares the single <see cref="NodeChatPersistenceWriter" /> so reads
///     serialize against in-flight writes on the same write key.
/// </summary>
internal sealed class NodeChatReadModel
{
    private readonly NodeChatPersistenceWriter _writer;

    public NodeChatReadModel(NodeChatPersistenceWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    public async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.IncludeArchived
            ? await ListAllConversationsAsync(request, cancellationToken)
            : await ListActiveConversationsAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return ReadConversationAsync(conversationId, capPayloadsToCompactionBoundary: false, cancellationToken);
    }

    /// <summary>
    ///     The chat-turn read: identical to <see cref="GetConversationAsync" /> except that when the conversation carries
    ///     a compaction synopsis, the content and metadata blobs of NON-user messages at or below the covered sequence are
    ///     not transferred, decrypted or parsed.
    /// </summary>
    /// <remarks>
    ///     It is a load-side cap on dead work: compaction shapes what a turn SENDS, not what it LOADS. It is
    ///     output-equivalent because both consumers of a turn conversation ignore the omitted payloads, and structure
    ///     always loads in full, so <see cref="SelectedPathResolver" /> resolves an identical path. Anything that
    ///     renders or re-persists a conversation uses <see cref="GetConversationAsync" /> — see
    ///     <c>docs/wiki/05-chat.md</c>, "The turn-scoped read".
    /// </remarks>
    public Task<NodeChatConversationDto?> GetConversationForTurnAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return ReadConversationAsync(conversationId, capPayloadsToCompactionBoundary: true, cancellationToken);
    }

    private async Task<NodeChatConversationDto?> ReadConversationAsync(Guid conversationId, bool capPayloadsToCompactionBoundary, CancellationToken cancellationToken)
    {
        return await _writer.ExecuteConversationSharedAsync(conversationId,
            async (dbContext, token) =>
            {
                await using var conversationCommand = dbContext.Database.GetDbConnection().CreateCommand();
                conversationCommand.CommandText = """
                                                  SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, selected_path_json, agent_definition_id, memory_excluded, compaction_summary, compaction_summary_covers_to_sequence, compaction_summary_updated_at_utc, conversation_state, conversation_state_covers_to_sequence, conversation_state_updated_at_utc
                                                  FROM conversations
                                                  WHERE conversation_id = $conversation_id AND purged = 0;
                                                  """;
                AddParameter(conversationCommand, "$conversation_id", conversationId);

                await OpenIfNeededAsync(conversationCommand.Connection, token);
                await using var conversationReader = await conversationCommand.ExecuteReaderAsync(token);
                if (!await conversationReader.ReadAsync(token))
                {
                    return null;
                }

                // Title is stored as an encrypted BLOB; read raw bytes and decrypt via the db-context gateway
                // (mirrors ReadConversationSummariesAsync in NodeChatPersistenceSql).
                var titleBytes = await conversationReader.IsDBNullAsync(ordinal: 1, token)
                    ? null
                    : await conversationReader.GetFieldValueAsync<byte[]>(ordinal: 1, token);

                // compaction_summary is an encrypted BLOB; decrypt via the same db-context gateway used for the title.
                var compactionSummary = dbContext.DecryptConversationCompactionSummary(await conversationReader.IsDBNullAsync(ordinal: 13, token)
                        ? null
                        : await conversationReader.GetFieldValueAsync<byte[]>(ordinal: 13, token),
                    conversationId);
                var compactionCoversToSequence = await conversationReader.IsDBNullAsync(ordinal: 14, token)
                    ? (int?)null
                    : conversationReader.GetInt32(14);

                // The cap fires only under the SAME condition ConversationContextBuilder.Build drops covered messages
                // on, so a conversation that has never been compacted loads byte-for-byte what it always did.
                var omitNonUserPayloadsAtOrBelowSequence = capPayloadsToCompactionBoundary && compactionSummary is { Length: > 0 }
                    ? compactionCoversToSequence
                    : null;

                var dto = new NodeChatConversationDto
                {
                    ConversationId = Guid.Parse(conversationReader.GetString(0)),
                    Title = DecryptTitle(titleBytes, dbContext, conversationId),
                    UserId = await conversationReader.IsDBNullAsync(ordinal: 2, token) ? null : conversationReader.GetString(2),
                    CreatedAtUtc = conversationReader.GetInt64(3),
                    LastSeenUtc = conversationReader.GetInt64(4),
                    Purged = conversationReader.GetBoolean(5),
                    Messages = await ReadMessagesAsync(dbContext, conversationId, token, omitNonUserPayloadsAtOrBelowSequence),
                    Origin = conversationReader.GetString(6),
                    IsPinned = conversationReader.GetBoolean(7),
                    Archived = conversationReader.GetBoolean(8),
                    BranchOfConversationId = await conversationReader.IsDBNullAsync(ordinal: 9, token) ? null : Guid.Parse(conversationReader.GetString(9)),
                    SelectedPath = DeserializeSelectedPath(await conversationReader.IsDBNullAsync(ordinal: 10, token) ? null : conversationReader.GetString(10)),
                    AgentDefinitionId = await conversationReader.IsDBNullAsync(ordinal: 11, token) ? null : Guid.Parse(conversationReader.GetString(11)),
                    MemoryExcluded = conversationReader.GetBoolean(12),
                    CompactionSummary = compactionSummary,
                    CompactionSummaryCoversToSequence = compactionCoversToSequence,
                    CompactionSummaryUpdatedAtUtc = await conversationReader.IsDBNullAsync(ordinal: 15, token) ? null : conversationReader.GetInt64(15),
                    ConversationState = dbContext.DecryptConversationState(await conversationReader.IsDBNullAsync(ordinal: 16, token)
                            ? null
                            : await conversationReader.GetFieldValueAsync<byte[]>(ordinal: 16, token),
                        conversationId),
                    ConversationStateCoversToSequence = await conversationReader.IsDBNullAsync(ordinal: 17, token) ? null : conversationReader.GetInt32(17),
                    ConversationStateUpdatedAtUtc = await conversationReader.IsDBNullAsync(ordinal: 18, token) ? null : conversationReader.GetInt64(18)
                };

                return dto;
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListActiveConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken)
    {
        return await _writer.ExecuteConversationSharedAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      SELECT c.conversation_id, c.title, c.created_at_utc, c.last_seen_utc, c.purged,
                                             m.content, m.status, c.origin, c.is_pinned, c.archived, m.message_id
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      WHERE c.purged = 0 AND c.archived = 0 AND c.kind = 'chat'
                                      ORDER BY c.is_pinned DESC, c.last_seen_utc DESC
                                      LIMIT $limit;
                                      """;
                return await ReadConversationSummariesAsync(command, dbContext, request.Limit, token);
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListAllConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken)
    {
        return await _writer.ExecuteConversationSharedAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      SELECT c.conversation_id, c.title, c.created_at_utc, c.last_seen_utc, c.purged,
                                             m.content, m.status, c.origin, c.is_pinned, c.archived, m.message_id
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      WHERE c.purged = 0 AND c.kind = 'chat'
                                      ORDER BY c.is_pinned DESC, c.last_seen_utc DESC
                                      LIMIT $limit;
                                      """;
                return await ReadConversationSummariesAsync(command, dbContext, request.Limit, token);
            },
            cancellationToken);
    }
}
