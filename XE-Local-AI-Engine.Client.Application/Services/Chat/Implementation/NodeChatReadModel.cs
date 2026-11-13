namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using XE_Local_AI_Engine.Client.Persistence;
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
                                                  SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, selected_path_json, agent_definition_id, memory_excluded, compaction_summary, compaction_summary_covers_to_sequence, compaction_summary_updated_at_utc, conversation_state, conversation_state_covers_to_sequence, conversation_state_updated_at_utc, is_plain, plain_system_prompt, sampling_options_json
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
                    ConversationStateUpdatedAtUtc = await conversationReader.IsDBNullAsync(ordinal: 18, token) ? null : conversationReader.GetInt64(18),
                    IsPlain = conversationReader.GetBoolean(19),
                    PlainSystemPrompt = dbContext.DecryptConversationPlainSystemPrompt(await conversationReader.IsDBNullAsync(ordinal: 20, token)
                            ? null
                            : await conversationReader.GetFieldValueAsync<byte[]>(ordinal: 20, token),
                        conversationId),
                    SamplingOptions = DeserializeSamplingOptions(await conversationReader.IsDBNullAsync(ordinal: 21, token) ? null : conversationReader.GetString(21), dbContext.GetService<ILoggerFactory>(), conversationId)
                };

                return dto;
            },
            cancellationToken);
    }

    /// <summary>Searches chat conversations' titles and message text in the in-memory index, after re-reading every conversation written since the last search.</summary>
    public async Task<NodeChatConversationSearchResultDto> SearchConversationsAsync(NodeChatSearchConversationsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var index = _writer.SearchIndex;
        // Read before the refresh: a backfill that finishes during it is then reported one search late, never early.
        var indexing = !index.IsBackfillComplete;
        await index.RefreshGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (conversationId, stamp) in index.SnapshotStale())
            {
                ConversationSearchDocument? document;
                try
                {
                    document = await ReadSearchDocumentAsync(conversationId, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One unreadable chat must not fail every search: its stale mark stays, so the next search retries it.
                    _writer.Logger?.LogWarning("Conversation {ConversationId} could not be read for search ({ExceptionType}); the next search retries it.",
                        conversationId, exception.GetType().Name);
                    continue;
                }

                index.Store(conversationId, document);
                if (index.AfterRefreshStore is { } hook)
                {
                    await hook(conversationId);
                }

                index.ClearStale(conversationId, stamp);
            }
        }
        finally
        {
            index.RefreshGate.Release();
        }

        return new NodeChatConversationSearchResultDto
        {
            Items = index.Search(request.Query, request.IncludeArchived, request.Limit),
            Indexing = indexing
        };
    }

    /// <summary>
    ///     Indexes every stored chat conversation not yet indexed, newest first, in batches that each hold the refresh
    ///     gate briefly. Returns the ids of the conversations it could not read; the rest of the pass still completes.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> BackfillSearchIndexAsync(int batchSize, CancellationToken cancellationToken)
    {
        var index = _writer.SearchIndex;
        var skipped = new List<Guid>();
        var conversationIds = await _writer.ExecuteConversationSharedAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT conversation_id FROM conversations WHERE purged = 0 AND kind = 'chat' ORDER BY last_seen_utc DESC;";
                await OpenIfNeededAsync(command.Connection, token);
                await using var reader = await command.ExecuteReaderAsync(token);
                var ids = new List<Guid>();
                while (await reader.ReadAsync(token))
                {
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }

                return ids;
            },
            cancellationToken);

        foreach (var batch in conversationIds.Chunk(batchSize))
        {
            await index.RefreshGate.WaitAsync(cancellationToken);
            try
            {
                // A conversation a search already refreshed is current, or stale-marked again by a later write.
                foreach (var conversationId in batch.Where(id => !index.Contains(id)))
                {
                    ConversationSearchDocument? document;
                    try
                    {
                        document = await ReadSearchDocumentAsync(conversationId, cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // One unreadable chat must not keep the rest of the pass from completing; the caller retries it.
                        skipped.Add(conversationId);
                        continue;
                    }

                    index.Store(conversationId, document);
                }
            }
            finally
            {
                index.RefreshGate.Release();
            }
        }

        index.MarkBackfillComplete();
        return skipped;
    }

    // Null when the conversation is gone, purged or not a chat, which drops it from the index. Only user and assistant text is indexed.
    private async Task<ConversationSearchDocument?> ReadSearchDocumentAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        return await _writer.ExecuteConversationSharedAsync(conversationId,
            async (dbContext, token) =>
            {
                await using var conversationCommand = dbContext.Database.GetDbConnection().CreateCommand();
                conversationCommand.CommandText = """
                                                  SELECT title, created_at_utc, last_seen_utc, origin, is_pinned, archived
                                                  FROM conversations
                                                  WHERE conversation_id = $conversation_id AND purged = 0 AND kind = 'chat';
                                                  """;
                AddParameter(conversationCommand, "$conversation_id", conversationId);
                await OpenIfNeededAsync(conversationCommand.Connection, token);

                string? title;
                long createdAtUtc;
                long lastSeenUtc;
                string origin;
                bool isPinned;
                bool archived;
                await using (var conversationReader = await conversationCommand.ExecuteReaderAsync(token))
                {
                    if (!await conversationReader.ReadAsync(token))
                    {
                        return null;
                    }

                    var titleBytes = await conversationReader.IsDBNullAsync(ordinal: 0, token)
                        ? null
                        : await conversationReader.GetFieldValueAsync<byte[]>(ordinal: 0, token);
                    title = DecryptForSearch(dbContext, conversationId, () => DecryptTitle(titleBytes, dbContext, conversationId));
                    createdAtUtc = conversationReader.GetInt64(1);
                    lastSeenUtc = conversationReader.GetInt64(2);
                    origin = conversationReader.GetString(3);
                    isPinned = conversationReader.GetBoolean(4);
                    archived = conversationReader.GetBoolean(5);
                }

                await using var messageCommand = dbContext.Database.GetDbConnection().CreateCommand();
                messageCommand.CommandText = """
                                             SELECT message_id, content FROM messages
                                             WHERE conversation_id = $conversation_id AND lower(role) IN ('user', 'assistant')
                                             ORDER BY sequence DESC;
                                             """;
                AddParameter(messageCommand, "$conversation_id", conversationId);
                await using var messageReader = await messageCommand.ExecuteReaderAsync(token);
                // Newest first, so the per-conversation cap keeps the latest text and older messages past it are never decrypted.
                var texts = new List<string>();
                var chars = 0L;
                while (chars < ConversationSearchIndex.MaxIndexedCharsPerConversation && await messageReader.ReadAsync(token))
                {
                    var messageId = Guid.Parse(messageReader.GetString(0));
                    var stored = await messageReader.GetFieldValueAsync<byte[]>(ordinal: 1, token);
                    var text = DecryptForSearch(dbContext, conversationId, () => dbContext.DecryptMessageContent(stored, conversationId, messageId)) ?? string.Empty;
                    texts.Add(text);
                    chars += text.Length;
                }

                return new ConversationSearchDocument
                {
                    Title = title,
                    Content = ConversationSearchIndex.JoinContent(texts),
                    CreatedAtUtc = createdAtUtc,
                    LastSeenUtc = lastSeenUtc,
                    Origin = origin,
                    IsPinned = isPinned,
                    Archived = archived
                };
            },
            cancellationToken);
    }

    // One undecryptable value must not hide the rest of the conversation from search, mirroring the list's untitled fallback.
    private static string? DecryptForSearch(NodeChatDbContext dbContext, Guid conversationId, Func<string?> decrypt)
    {
        try
        {
            return decrypt();
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            dbContext.GetService<ILoggerFactory>()
                     .CreateLogger<NodeChatReadModel>()
                     .LogWarning(exception, "Conversation {ConversationId} has a title or message that could not be decrypted; search skips it.", conversationId);
            return null;
        }
    }

    // The status is the last message's; the preview skips a failed assistant reply, whose content is empty. When the preview is
    // blank, as a stopped or running turn's is, the reader falls back to the latest user message (u): only it can see through the encryption.
    private async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListActiveConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken)
    {
        return await _writer.ExecuteConversationSharedAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      SELECT c.conversation_id, c.title, c.created_at_utc, c.last_seen_utc, c.purged,
                                             p.content, m.status, c.origin, c.is_pinned, c.archived, p.message_id,
                                             u.content, u.message_id
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      LEFT JOIN messages p ON p.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                            AND NOT (mi.role = 'assistant' AND mi.status = 'failed')
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      LEFT JOIN messages u ON u.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id AND mi.role = 'user'
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
                                             p.content, m.status, c.origin, c.is_pinned, c.archived, p.message_id,
                                             u.content, u.message_id
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      LEFT JOIN messages p ON p.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                            AND NOT (mi.role = 'assistant' AND mi.status = 'failed')
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      LEFT JOIN messages u ON u.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id AND mi.role = 'user'
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
