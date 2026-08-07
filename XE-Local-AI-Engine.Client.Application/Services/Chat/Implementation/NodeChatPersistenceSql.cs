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
///     row read/probe queries every collaborator reuses. Pure functions over a caller-supplied
///     <see cref="NodeChatDbContext" />; consumed via <c>using static</c>. The serialization of content/metadata is
///     delegated to <see cref="NodeChatMetadataSerializer" />.
/// </summary>
internal static class NodeChatPersistenceSql
{
    // Opens the node chat connection if needed AND applies the WAL/busy_timeout/synchronous pragmas on the open (AUD4-08).
    // This is the single choke point every raw-ADO node-chat read/write routes through, so it is where the raw path gets
    // the same connection posture the EF interceptor applies to EF-initiated opens.
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
    ///     Allocates the next contiguous sequence for a conversation as <c>MAX(sequence)+1</c>. The read must run inside
    ///     the same transaction as the insert that consumes it (and under the conversation-exclusive write lock), or two
    ///     concurrent inserts observe the same maximum and collide on the unique <c>(conversation_id, sequence)</c> index.
    /// </summary>
    internal static async Task<int> NextSequenceAsync(NodeChatDbContext dbContext, Guid conversationId, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence), -1) + 1 FROM messages WHERE conversation_id = $conversation_id;";
        AddParameter(command, "$conversation_id", conversationId);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    // Extended result code raised by SQLite when an insert violates a UNIQUE index. The value 2067 is
    // SQLITE_CONSTRAINT_UNIQUE. On the messages table the only unique index covers conversation id plus sequence, so
    // this result identifies a sequence collision. A duplicate primary key surfaces as SQLITE_CONSTRAINT_PRIMARYKEY
    // 1555 instead and is deliberately NOT retried — that is a genuine duplicate message id, not an allocation race.
    private const int SqliteConstraintUnique = 2067;

    // Defense in depth: the conversation-exclusive write lock already makes in-process sequence allocation race-free, so
    // a unique-index conflict can only come from a second OS process on the same database file. Re-read MAX(sequence)
    // and retry a bounded number of times before surfacing the failure.
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

        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new NodeChatMessageFeedbackDto(Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            await reader.IsDBNullAsync(ordinal: 3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    internal static async Task<NodeChatConversationDto?> ReadConversationWithMessagesAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await ReadConversationRowAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false);
        return conversation is null
            ? null
            : conversation with
            {
                Messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false)
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

        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var titleBytes = await reader.IsDBNullAsync(ordinal: 1, cancellationToken).ConfigureAwait(false)
            ? null
            : await reader.GetFieldValueAsync<byte[]>(ordinal: 1, cancellationToken).ConfigureAwait(false);
        return new NodeChatConversationDto(Guid.Parse(reader.GetString(0)),
            DecryptTitle(titleBytes, dbContext, conversationId),
            await reader.IsDBNullAsync(ordinal: 2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            [],
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            await reader.IsDBNullAsync(ordinal: 9, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(9)),
            AgentDefinitionId: await reader.IsDBNullAsync(ordinal: 10, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(10)),
            MemoryExcluded: reader.GetBoolean(11),
            CompactionSummary: dbContext.DecryptConversationCompactionSummary(
                await reader.IsDBNullAsync(ordinal: 12, cancellationToken).ConfigureAwait(false) ? null : await reader.GetFieldValueAsync<byte[]>(ordinal: 12, cancellationToken).ConfigureAwait(false),
                conversationId),
            CompactionSummaryCoversToSequence: await reader.IsDBNullAsync(ordinal: 13, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt32(13),
            CompactionSummaryUpdatedAtUtc: await reader.IsDBNullAsync(ordinal: 14, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(14));
    }

    /// <summary>
    ///     Reads a single message. Filters in SQL rather than materializing the whole conversation and picking one out of
    ///     it: this sits on the streaming partial-flush path (<c>UpdateCorrelatedMessageAsync</c>, ~10 calls a second for
    ///     the length of a turn), where the previous shape AEAD-decrypted and JSON-parsed EVERY message in the
    ///     conversation to return one — making each flush cost grow with conversation length.
    ///     <para>
    ///         Precisely: the decrypt/deserialize work drops to a single row. The row SCAN is still bounded by the
    ///         conversation's <c>IX_messages_conversation_id</c> range rather than seeking the primary key, because the
    ///         shared query's <c>$message_id IS NULL OR …</c> guard is not sargable. That is deliberate — reusing one
    ///         query is what keeps the two reads projection-identical, and the scan was never the expensive part.
    ///     </para>
    /// </summary>
    internal static async Task<NodeChatPersistedMessageDto?> ReadMessageAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken, filterMessageId: messageId).ConfigureAwait(false);
        return messages.Count > 0 ? messages[0] : null;
    }

    /// <summary>
    ///     Reads every message of a conversation, ordered by sequence.
    /// </summary>
    /// <param name="omitNonUserPayloadsAtOrBelowSequence">
    ///     Load-side cap for the chat-turn read (see <c>NodeChatReadModel.GetConversationForTurnAsync</c>). When set, the
    ///     encrypted <c>content</c> and <c>metadata_json</c> blobs of every NON-user message at or below this sequence are
    ///     selected as NULL, so they are neither transferred, AEAD-decrypted, nor JSON-parsed; their content surfaces as
    ///     <see cref="string.Empty" /> and their metadata-derived fields as null. Structure (id, sequence, role, variant
    ///     group, timestamps) is always loaded in full, so selected-path resolution is unaffected. <c>null</c> — the
    ///     default every other caller uses — loads everything, exactly as before.
    /// </param>
    /// <param name="filterMessageId">
    ///     When set, restricts the read to that one message (see <see cref="ReadMessageAsync" />). Sharing this method
    ///     rather than writing a second query is what guarantees the single-message read projects an identical DTO.
    /// </param>
    internal static async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ReadMessagesAsync(NodeChatDbContext dbContext,
        Guid conversationId,
        CancellationToken cancellationToken,
        int? omitNonUserPayloadsAtOrBelowSequence = null,
        Guid? filterMessageId = null)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        // LEFT JOIN the node-local feedback row so the conversation read carries each message's feedback state
        // (rating/comment) inline — the client derives feedback from the message instead of a per-message GET.
        // The two CASE expressions apply the optional load-side payload cap documented above; with the parameter null
        // (every caller but the chat turn) both collapse to a plain column read. `lower(role)` is the conservative
        // direction: an unexpected casing keeps the payload rather than dropping it.
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

        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<NodeChatPersistedMessageDto>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var messageId = Guid.Parse(reader.GetString(0));
            var messageConversationId = Guid.Parse(reader.GetString(1));
            var metadataJson = await reader.IsDBNullAsync(ordinal: 6, cancellationToken).ConfigureAwait(false)
                ? null
                : dbContext.DecryptMessageMetadata(await reader.GetFieldValueAsync<byte[]>(ordinal: 6, cancellationToken).ConfigureAwait(false), messageConversationId, messageId);
            var metadata = DeserializeMetadata(metadataJson);
            // The column is NOT NULL in the schema, so a null here can only be the load-side payload cap above electing
            // not to transfer this message's content — which the cap's callers have proven they never read.
            var content = await reader.IsDBNullAsync(ordinal: 5, cancellationToken).ConfigureAwait(false)
                ? string.Empty
                : dbContext.DecryptMessageContent(await reader.GetFieldValueAsync<byte[]>(ordinal: 5, cancellationToken).ConfigureAwait(false), messageConversationId, messageId);
            messages.Add(new NodeChatPersistedMessageDto(messageId,
                messageConversationId,
                await reader.IsDBNullAsync(ordinal: 2, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetString(4),
                content,
                metadata.Reasoning,
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                metadata.Model,
                await reader.IsDBNullAsync(ordinal: 10, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(10),
                metadata.MetadataJson,
                metadata.InputCount,
                metadata.OutputCount,
                metadata.TotalCount,
                metadata.ReasoningCount,
                reader.GetString(11),
                await reader.IsDBNullAsync(ordinal: 12, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(12)),
                await reader.IsDBNullAsync(ordinal: 13, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(13)),
                await reader.IsDBNullAsync(ordinal: 14, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(14),
                await reader.IsDBNullAsync(ordinal: 15, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(15),
                metadata.Parts,
                metadata.AgentDefinitionId,
                metadata.AgentName,
                metadata.ReasoningEffort,
                metadata.GenerationDurationMs,
                metadata.Sources));
        }

        return messages;
    }

    internal static async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ReadConversationSummariesAsync(DbCommand command, NodeChatDbContext dbContext, int? limit,
        CancellationToken cancellationToken)
    {
        AddParameter(command, "$limit", limit is > 0 ? limit.Value : int.MaxValue);

        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var conversations = new List<NodeChatConversationSummaryDto>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var convId = Guid.Parse(reader.GetString(0));
            var titleBytes = await reader.IsDBNullAsync(ordinal: 1, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<byte[]>(ordinal: 1, cancellationToken).ConfigureAwait(false);
            // The preview content column (ordinal 5) is decrypted read-both against the previewed message's id
            // (ordinal 10); a LEFT JOIN with no message leaves both NULL.
            var content = await reader.IsDBNullAsync(ordinal: 5, cancellationToken).ConfigureAwait(false)
                ? null
                : dbContext.DecryptMessageContent(await reader.GetFieldValueAsync<byte[]>(ordinal: 5, cancellationToken).ConfigureAwait(false),
                    convId,
                    Guid.Parse(reader.GetString(10)));
            conversations.Add(new NodeChatConversationSummaryDto(convId,
                DecryptTitle(titleBytes, dbContext, convId),
                reader.GetInt64(2),
                reader.GetInt64(3),
                Preview(content),
                await reader.IsDBNullAsync(ordinal: 6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                reader.GetBoolean(4),
                reader.GetString(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9)));
        }

        return conversations;
    }

    internal static async Task TouchConversationAsync(NodeChatDbContext dbContext, Guid conversationId, long lastSeenUtc, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET last_seen_utc = {0} WHERE conversation_id = {1};",
            [lastSeenUtc, conversationId],
            cancellationToken).ConfigureAwait(false);
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
