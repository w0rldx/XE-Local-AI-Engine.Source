namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static NodeChatMetadataSerializer;

/// <summary>
///     Shared raw-ADO helpers for the node chat persistence path: low-level <see cref="DbCommand" /> wiring plus the
///     row read/probe queries every collaborator reuses. Pure functions over a caller-supplied
///     <see cref="NodeChatDbContext" />; consumed via <c>using static</c>. The serialization of content/metadata is
///     delegated to <see cref="NodeChatMetadataSerializer" />.
/// </summary>
internal static class NodeChatPersistenceSql
{
    internal static async Task OpenIfNeededAsync(DbConnection? connection, CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            throw new InvalidOperationException("The node chat database connection was not available.");
        }

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = DbValue(value);
        command.Parameters.Add(parameter);
    }

    internal static async Task<int> NextSequenceAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), -1) + 1 FROM messages WHERE conversation_id = $conversation_id;";
        AddParameter(command, "$conversation_id", conversationId);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
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
            await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
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
                              SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, agent_definition_id, memory_excluded
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

        var titleBytes = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
            ? null
            : await reader.GetFieldValueAsync<byte[]>(1, cancellationToken).ConfigureAwait(false);
        return new NodeChatConversationDto(Guid.Parse(reader.GetString(0)),
            DecryptTitle(titleBytes, dbContext, conversationId),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            [],
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(9)),
            AgentDefinitionId: await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(10)),
            MemoryExcluded: reader.GetBoolean(11));
    }

    internal static async Task<NodeChatPersistedMessageDto?> ReadMessageAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false);
        return messages.SingleOrDefault(message => message.MessageId == messageId);
    }

    internal static async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ReadMessagesAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        // LEFT JOIN the node-local feedback row so the conversation read carries each message's feedback state
        // (rating/comment) inline — the client derives feedback from the message instead of a per-message GET.
        command.CommandText = """
                              SELECT m.message_id, m.conversation_id, m.request_id, m.sequence, m.role, m.content, m.metadata_json, m.status, m.created_at_utc, m.updated_at_utc, m.error, m.origin, m.parent_message_id, m.variant_group_id, f.rating, f.comment
                              FROM messages m
                              LEFT JOIN message_feedback f ON f.message_id = m.message_id
                              WHERE m.conversation_id = $conversation_id
                              ORDER BY m.sequence ASC;
                              """;
        AddParameter(command, "$conversation_id", conversationId);

        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<NodeChatPersistedMessageDto>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metadataJson = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false)
                ? null
                : Decode(await reader.GetFieldValueAsync<byte[]>(6, cancellationToken).ConfigureAwait(false));
            var metadata = DeserializeMetadata(metadataJson);
            messages.Add(new NodeChatPersistedMessageDto(Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(2)),
                reader.GetInt32(3),
                reader.GetString(4),
                Decode(await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false)),
                metadata.Reasoning,
                reader.GetString(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                metadata.Model,
                await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(10),
                metadata.MetadataJson,
                metadata.InputCount,
                metadata.OutputCount,
                metadata.TotalCount,
                metadata.ReasoningCount,
                reader.GetString(11),
                await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(12)),
                await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(13)),
                await reader.IsDBNullAsync(14, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(14),
                await reader.IsDBNullAsync(15, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(15),
                metadata.Parts,
                metadata.AgentDefinitionId,
                metadata.AgentName,
                metadata.ReasoningEffort,
                metadata.GenerationDurationMs));
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
            var titleBytes = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<byte[]>(1, cancellationToken).ConfigureAwait(false);
            var content = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? null
                : Decode(await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false));
            conversations.Add(new NodeChatConversationSummaryDto(convId,
                DecryptTitle(titleBytes, dbContext, convId),
                reader.GetInt64(2),
                reader.GetInt64(3),
                Preview(content),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
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
