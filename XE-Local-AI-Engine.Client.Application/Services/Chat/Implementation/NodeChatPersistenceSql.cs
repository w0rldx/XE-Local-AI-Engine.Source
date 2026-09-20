namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using static NodeChatMetadataSerializer;

/// <summary>
///     Shared raw-ADO helpers for the node chat persistence path: low-level <see cref="DbCommand" /> wiring plus the
///     row read and probe queries every collaborator reuses.
/// </summary>
/// <remarks>
///     Pure functions over a caller-supplied <see cref="NodeChatDbContext" />, consumed via <c>using static</c>.
///     Content and metadata serialization is delegated to <see cref="NodeChatMetadataSerializer" />.
/// </remarks>
internal static class NodeChatPersistenceSql
{
    // Opens the connection if needed AND applies the WAL, busy_timeout and synchronous pragmas. Every raw-ADO
    // node-chat read and write routes through here, so the raw path gets the EF interceptor's connection posture.
    internal static Task OpenIfNeededAsync(DbConnection? connection, CancellationToken cancellationToken)
    {
        return NodeSqlitePragmas.OpenAndConfigureAsync(connection, cancellationToken);
    }

    internal static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = DbValue(value);
        command.Parameters.Add(parameter);
    }

    /// <summary>
    ///     Allocates the next contiguous sequence for a conversation as <c>MAX(sequence)+1</c>.
    /// </summary>
    /// <remarks>
    ///     The read must run inside the same transaction as the insert that consumes it, and under the
    ///     conversation-exclusive write lock, or two concurrent inserts observe the same maximum and collide on the
    ///     unique <c>(conversation_id, sequence)</c> index.
    /// </remarks>
    internal static async Task<int> NextSequenceAsync(NodeChatDbContext dbContext, Guid conversationId, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence), -1) + 1 FROM messages WHERE conversation_id = $conversation_id;";
        AddParameter(command, "$conversation_id", conversationId);
        await OpenIfNeededAsync(command.Connection, cancellationToken);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    // SQLITE_CONSTRAINT_UNIQUE. The messages table's only unique index covers conversation id plus sequence, so this
    // identifies a sequence collision; a duplicate primary key surfaces as 1555 and is deliberately NOT retried.
    private const int SqliteConstraintUnique = 2067;

    // Defense in depth: the conversation-exclusive write lock already makes in-process allocation race-free, so a
    // unique-index conflict can only be a second OS process. Re-read MAX(sequence) a bounded number of times.
    internal const int MaxSequenceAllocationAttempts = 5;

    internal static bool IsUniqueConstraintViolation(Exception exception)
    {
        return exception is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique };
    }

    internal static async Task<NodeChatMessageFeedbackDto?> ReadFeedbackAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT message_id, conversation_id, rating, comment, created_at_utc, updated_at_utc
                              FROM message_feedback
                              WHERE conversation_id = $conversation_id AND message_id = $message_id;
                              """;
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$message_id", messageId);

        await OpenIfNeededAsync(command.Connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new NodeChatMessageFeedbackDto
        {
            MessageId = Guid.Parse(reader.GetString(0)),
            ConversationId = Guid.Parse(reader.GetString(1)),
            Rating = reader.GetString(2),
            Comment = await reader.IsDBNullAsync(ordinal: 3, cancellationToken) ? null : reader.GetString(3),
            CreatedAtUtc = reader.GetInt64(4),
            UpdatedAtUtc = reader.GetInt64(5)
        };
    }

    internal static async Task<NodeChatConversationDto?> ReadConversationWithMessagesAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await ReadConversationRowAsync(dbContext, conversationId, cancellationToken);
        return conversation is null
            ? null
            : conversation with
            {
                Messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken)
            };
    }

    internal static async Task<NodeChatConversationDto?> ReadConversationRowAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, agent_definition_id, memory_excluded, compaction_summary, compaction_summary_covers_to_sequence, compaction_summary_updated_at_utc
                              FROM conversations
                              WHERE conversation_id = $conversation_id;
                              """;
        AddParameter(command, "$conversation_id", conversationId);

        await OpenIfNeededAsync(command.Connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var titleBytes = await reader.IsDBNullAsync(ordinal: 1, cancellationToken)
            ? null
            : await reader.GetFieldValueAsync<byte[]>(ordinal: 1, cancellationToken);
        return new NodeChatConversationDto
        {
            ConversationId = Guid.Parse(reader.GetString(0)),
            Title = DecryptTitle(titleBytes, dbContext, conversationId),
            UserId = await reader.IsDBNullAsync(ordinal: 2, cancellationToken) ? null : reader.GetString(2),
            CreatedAtUtc = reader.GetInt64(3),
            LastSeenUtc = reader.GetInt64(4),
            Purged = reader.GetBoolean(5),
            Messages = [],
            Origin = reader.GetString(6),
            IsPinned = reader.GetBoolean(7),
            Archived = reader.GetBoolean(8),
            BranchOfConversationId = await reader.IsDBNullAsync(ordinal: 9, cancellationToken) ? null : Guid.Parse(reader.GetString(9)),
            AgentDefinitionId = await reader.IsDBNullAsync(ordinal: 10, cancellationToken) ? null : Guid.Parse(reader.GetString(10)),
            MemoryExcluded = reader.GetBoolean(11),
            CompactionSummary = dbContext.DecryptConversationCompactionSummary(
                await reader.IsDBNullAsync(ordinal: 12, cancellationToken) ? null : await reader.GetFieldValueAsync<byte[]>(ordinal: 12, cancellationToken),
                conversationId),
            CompactionSummaryCoversToSequence = await reader.IsDBNullAsync(ordinal: 13, cancellationToken) ? null : reader.GetInt32(13),
            CompactionSummaryUpdatedAtUtc = await reader.IsDBNullAsync(ordinal: 14, cancellationToken) ? null : reader.GetInt64(14)
        };
    }

    /// <summary>
    ///     Reads a single message, filtering in SQL rather than materializing the whole conversation.
    /// </summary>
    /// <remarks>
    ///     This sits on the streaming partial-flush path, so decrypting and parsing every message to return one makes
    ///     each flush cost grow with conversation length. The decrypt work therefore drops to a single row, while the
    ///     SCAN stays bounded by the conversation's index range rather than seeking the primary key, because the
    ///     shared query's <c>$message_id IS NULL OR …</c> guard is not sargable — deliberate, since reusing one query
    ///     keeps the two reads projection-identical and the scan was never the expensive part.
    /// </remarks>
    internal static async Task<NodeChatPersistedMessageDto?> ReadMessageAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken, filterMessageId: messageId);
        return messages.Count > 0 ? messages[0] : null;
    }

    /// <summary>
    ///     Reads every message of a conversation, ordered by sequence.
    /// </summary>
    /// <param name="omitNonUserPayloadsAtOrBelowSequence">
    ///     Load-side cap: the payload blobs of NON-user messages at or below it are NULL; <c>null</c> loads all.
    /// </param>
    /// <param name="filterMessageId">Restricts the read to one message (see <see cref="ReadMessageAsync" />).</param>
    /// <remarks>
    ///     A capped payload is neither transferred, AEAD-decrypted nor JSON-parsed; its content surfaces as
    ///     <see cref="string.Empty" /> and its metadata-derived fields as null, while structure always loads in full,
    ///     so selected-path resolution is unaffected. Sharing this method rather than writing a second query is what
    ///     guarantees the single-message read projects an identical DTO.
    /// </remarks>
    internal static async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ReadMessagesAsync(NodeChatDbContext dbContext,
        Guid conversationId,
        CancellationToken cancellationToken,
        int? omitNonUserPayloadsAtOrBelowSequence = null,
        Guid? filterMessageId = null)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        // LEFT JOIN the feedback row so the read carries each message's rating and comment inline. The two CASE
        // expressions apply the load-side payload cap, and `lower(role)` keeps a payload on unexpected casing.
        command.CommandText = """
                              SELECT m.message_id, m.conversation_id, m.request_id, m.sequence, m.role,
                                     CASE WHEN $omit_payloads_at_or_below IS NOT NULL AND m.sequence <= $omit_payloads_at_or_below AND lower(m.role) <> 'user'
                                          THEN NULL ELSE m.content END,
                                     CASE WHEN $omit_payloads_at_or_below IS NOT NULL AND m.sequence <= $omit_payloads_at_or_below AND lower(m.role) <> 'user'
                                          THEN NULL ELSE m.metadata_json END,
                                     m.status, m.created_at_utc, m.updated_at_utc, m.error, m.origin, m.parent_message_id, m.variant_group_id, f.rating, f.comment
                              FROM messages m
                              LEFT JOIN message_feedback f ON f.message_id = m.message_id
                              WHERE m.conversation_id = $conversation_id
                                AND ($message_id IS NULL OR m.message_id = $message_id)
                              ORDER BY m.sequence ASC;
                              """;
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$omit_payloads_at_or_below", omitNonUserPayloadsAtOrBelowSequence);
        AddParameter(command, "$message_id", filterMessageId);

        await OpenIfNeededAsync(command.Connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var messages = new List<NodeChatPersistedMessageDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var messageId = Guid.Parse(reader.GetString(0));
            var messageConversationId = Guid.Parse(reader.GetString(1));
            var metadataJson = await reader.IsDBNullAsync(ordinal: 6, cancellationToken)
                ? null
                : dbContext.DecryptMessageMetadata(await reader.GetFieldValueAsync<byte[]>(ordinal: 6, cancellationToken), messageConversationId, messageId);
            var metadata = DeserializeMetadata(metadataJson);
            // The column is NOT NULL in the schema, so a null here can only be the load-side payload cap above electing
            // not to transfer this message's content — which the cap's callers have proven they never read.
            var content = await reader.IsDBNullAsync(ordinal: 5, cancellationToken)
                ? string.Empty
                : dbContext.DecryptMessageContent(await reader.GetFieldValueAsync<byte[]>(ordinal: 5, cancellationToken), messageConversationId, messageId);
            messages.Add(new NodeChatPersistedMessageDto
            {
                MessageId = messageId,
                ConversationId = messageConversationId,
                RequestId = await reader.IsDBNullAsync(ordinal: 2, cancellationToken) ? null : Guid.Parse(reader.GetString(2)),
                Sequence = reader.GetInt32(3),
                Role = reader.GetString(4),
                Content = content,
                Reasoning = metadata.Reasoning,
                Status = reader.GetString(7),
                CreatedAtUtc = reader.GetInt64(8),
                UpdatedAtUtc = reader.GetInt64(9),
                Model = metadata.Model,
                Error = await reader.IsDBNullAsync(ordinal: 10, cancellationToken) ? null : reader.GetString(10),
                MetadataJson = metadata.MetadataJson,
                InputCount = metadata.InputCount,
                OutputCount = metadata.OutputCount,
                TotalCount = metadata.TotalCount,
                ReasoningCount = metadata.ReasoningCount,
                Origin = reader.GetString(11),
                ParentMessageId = await reader.IsDBNullAsync(ordinal: 12, cancellationToken) ? null : Guid.Parse(reader.GetString(12)),
                VariantGroupId = await reader.IsDBNullAsync(ordinal: 13, cancellationToken) ? null : Guid.Parse(reader.GetString(13)),
                FeedbackRating = await reader.IsDBNullAsync(ordinal: 14, cancellationToken) ? null : reader.GetString(14),
                FeedbackComment = await reader.IsDBNullAsync(ordinal: 15, cancellationToken) ? null : reader.GetString(15),
                Parts = metadata.Parts,
                AgentDefinitionId = metadata.AgentDefinitionId,
                AgentName = metadata.AgentName,
                ReasoningEffort = metadata.ReasoningEffort,
                GenerationDurationMs = metadata.GenerationDurationMs,
                Sources = metadata.Sources
            });
        }

        return messages;
    }

    internal static async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ReadConversationSummariesAsync(DbCommand command, NodeChatDbContext dbContext, int? limit,
        CancellationToken cancellationToken)
    {
        AddParameter(command, "$limit", limit is > 0 ? limit.Value : int.MaxValue);

        await OpenIfNeededAsync(command.Connection, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var conversations = new List<NodeChatConversationSummaryDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var convId = Guid.Parse(reader.GetString(0));
            var titleBytes = await reader.IsDBNullAsync(ordinal: 1, cancellationToken)
                ? null
                : await reader.GetFieldValueAsync<byte[]>(ordinal: 1, cancellationToken);
            // The preview content column (ordinal 5) is decrypted read-both against the previewed message's id
            // (ordinal 10); a LEFT JOIN with no message leaves both NULL.
            var content = await reader.IsDBNullAsync(ordinal: 5, cancellationToken)
                ? null
                : dbContext.DecryptMessageContent(await reader.GetFieldValueAsync<byte[]>(ordinal: 5, cancellationToken),
                    convId,
                    Guid.Parse(reader.GetString(10)));
            conversations.Add(new NodeChatConversationSummaryDto
            {
                ConversationId = convId,
                Title = DecryptTitle(titleBytes, dbContext, convId),
                CreatedAtUtc = reader.GetInt64(2),
                LastSeenUtc = reader.GetInt64(3),
                LastMessagePreview = Preview(content),
                LastMessageStatus = await reader.IsDBNullAsync(ordinal: 6, cancellationToken) ? null : reader.GetString(6),
                Purged = reader.GetBoolean(4),
                Origin = reader.GetString(7),
                IsPinned = reader.GetBoolean(8),
                Archived = reader.GetBoolean(9)
            });
        }

        return conversations;
    }

    internal static async Task TouchConversationAsync(NodeChatDbContext dbContext, Guid conversationId, long lastSeenUtc, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET last_seen_utc = {0} WHERE conversation_id = {1};",
            [lastSeenUtc, conversationId],
            cancellationToken);
    }

    internal static void ValidateCorrelation(NodeChatMessageCorrelation correlation)
    {
        if (correlation.ConversationId == Guid.Empty || correlation.MessageId == Guid.Empty || correlation.RequestId == Guid.Empty)
        {
            throw new ArgumentException("Conversation, message, and request ids are required for correlated chat persistence operations.", nameof(correlation));
        }
    }

    internal static bool IsTerminalStatus(string status)
    {
        return string.Equals(status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal)
               || string.Equals(status, NodeChatMessageStatusValues.Cancelled, StringComparison.Ordinal)
               || string.Equals(status, NodeChatMessageStatusValues.Failed, StringComparison.Ordinal)
               || string.Equals(status, NodeChatMessageStatusValues.Interrupted, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Encrypts a conversation title string for raw-SQL persistence via the db context. Returns null when the title
    ///     is null so the database column writes NULL.
    /// </summary>
    internal static byte[]? EncryptTitle(string? title, NodeChatDbContext dbContext, Guid conversationId)
    {
        return dbContext.EncryptConversationTitle(title, conversationId);
    }

    /// <summary>
    ///     Decrypts a raw title blob read from the database back to a string via the db context. Returns null when the
    ///     blob is null.
    /// </summary>
    internal static string? DecryptTitle(byte[]? encrypted, NodeChatDbContext dbContext, Guid conversationId)
    {
        return dbContext.DecryptConversationTitle(encrypted, conversationId);
    }

    private static object DbValue(object? value)
    {
        return value ?? DBNull.Value;
    }
}
