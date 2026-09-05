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
    ///     <para>
    ///         Compaction shapes what a turn SENDS, not what it LOADS, so a long conversation paid a full decrypt +
    ///         JSON-parse of its entire history before every first token — including the messages the synopsis had already
    ///         replaced. This is a load-side cap on exactly that dead work.
    ///     </para>
    ///     <para>
    ///         <strong>Why it is output-equivalent.</strong> Only two consumers read a turn conversation's messages, and
    ///         each provably ignores the omitted payloads: <c>ConversationContextBuilder.Build</c> drops every message at or below
    ///         the covered sequence outright (that IS the compaction filter), and <c>CollectUserTurns</c> keeps only
    ///         <c>role == "user"</c> messages, which the cap never touches at any sequence. Message STRUCTURE — id,
    ///         sequence, role, variant group, timestamps — is always loaded in full, so
    ///         <see cref="SelectedPathResolver" /> still sees every branch and resolves the identical selected path;
    ///         a variant group whose siblings straddle the boundary is therefore still resolved from the complete sibling
    ///         set, and only then filtered by sequence.
    ///     </para>
    ///     <para>
    ///         Use <see cref="GetConversationAsync" /> for anything that renders or re-persists a conversation (the UI
    ///         load, regeneration, branching, compaction itself) — those need every payload. A caller-managed
    ///         integration continuation uses it too, and for the same reason: with tool history on, the builder KEEPS a
    ///         covered assistant row for the tool parts persisted in its <c>metadata_json</c>, so the equivalence
    ///         argument above no longer holds for that caller.
    ///     </para>
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
                                                  SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, selected_path_json, agent_definition_id, memory_excluded, compaction_summary, compaction_summary_covers_to_sequence, compaction_summary_updated_at_utc
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

                // compaction_summary is an encrypted BLOB; decrypt via the same db-context gateway used for the title.
                var compactionSummary = dbContext.DecryptConversationCompactionSummary(await conversationReader.IsDBNullAsync(ordinal: 13, token).ConfigureAwait(false)
                        ? null
                        : await conversationReader.GetFieldValueAsync<byte[]>(ordinal: 13, token).ConfigureAwait(false),
                    conversationId);
                var compactionCoversToSequence = await conversationReader.IsDBNullAsync(ordinal: 14, token).ConfigureAwait(false)
                    ? (int?)null
                    : conversationReader.GetInt32(14);

                // The cap fires only under the SAME condition ConversationContextBuilder.Build uses to drop the covered
                // messages — a non-empty synopsis AND a covered sequence — so a conversation that has never been
                // compacted loads byte-for-byte what it always did.
                var omitNonUserPayloadsAtOrBelowSequence = capPayloadsToCompactionBoundary && compactionSummary is { Length: > 0 }
                    ? compactionCoversToSequence
                    : null;

                var dto = new NodeChatConversationDto(Guid.Parse(conversationReader.GetString(0)),
                    DecryptTitle(titleBytes, dbContext, conversationId),
                    await conversationReader.IsDBNullAsync(ordinal: 2, token).ConfigureAwait(false) ? null : conversationReader.GetString(2),
                    conversationReader.GetInt64(3),
                    conversationReader.GetInt64(4),
                    conversationReader.GetBoolean(5),
                    await ReadMessagesAsync(dbContext, conversationId, token, omitNonUserPayloadsAtOrBelowSequence).ConfigureAwait(false),
                    conversationReader.GetString(6),
                    conversationReader.GetBoolean(7),
                    conversationReader.GetBoolean(8),
                    await conversationReader.IsDBNullAsync(ordinal: 9, token).ConfigureAwait(false) ? null : Guid.Parse(conversationReader.GetString(9)),
                    DeserializeSelectedPath(await conversationReader.IsDBNullAsync(ordinal: 10, token).ConfigureAwait(false) ? null : conversationReader.GetString(10)),
                    await conversationReader.IsDBNullAsync(ordinal: 11, token).ConfigureAwait(false) ? null : Guid.Parse(conversationReader.GetString(11)),
                    conversationReader.GetBoolean(12),
                    compactionSummary,
                    compactionCoversToSequence,
                    await conversationReader.IsDBNullAsync(ordinal: 15, token).ConfigureAwait(false) ? null : conversationReader.GetInt64(15));

                return dto;
            },
            cancellationToken).ConfigureAwait(false);
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
                return await ReadConversationSummariesAsync(command, dbContext, request.Limit, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
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
                return await ReadConversationSummariesAsync(command, dbContext, request.Limit, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
