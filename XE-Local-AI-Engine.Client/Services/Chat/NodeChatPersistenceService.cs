namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;

public sealed class NodeChatPersistenceService(NodeChatPersistenceWriter writer) : INodeChatPersistenceService
{
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<NodeChatConversationDto> CreateConversationAsync(NodeChatCreateConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversationId = Guid.NewGuid();
        var createdAtUtc = request.CreatedAtUtc;

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(conversationId),
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged)
                                      VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0);
                                      """;
                AddParameter(command, "$conversation_id", conversationId);
                AddParameter(command, "$title", request.Title);
                AddParameter(command, "$user_id", request.UserId);
                AddParameter(command, "$created_at_utc", createdAtUtc);
                AddParameter(command, "$last_seen_utc", createdAtUtc);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return new NodeChatConversationDto(conversationId, request.Title, request.UserId, createdAtUtc, createdAtUtc, false, []);
            },
            cancellationToken).ConfigureAwait(false);
    }

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
                                                  SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged
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

                var dto = new NodeChatConversationDto(Guid.Parse(conversationReader.GetString(0)),
                    await conversationReader.IsDBNullAsync(1, token).ConfigureAwait(false) ? null : conversationReader.GetString(1),
                    await conversationReader.IsDBNullAsync(2, token).ConfigureAwait(false) ? null : conversationReader.GetString(2),
                    conversationReader.GetInt64(3),
                    conversationReader.GetInt64(4),
                    conversationReader.GetBoolean(5),
                    await ReadMessagesAsync(dbContext, conversationId, token).ConfigureAwait(false));

                return dto;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<NodeChatPersistedMessageDto> PersistUserMessageAsync(NodeChatPersistUserMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);

        return InsertMessageAsync(request.ConversationId,
            request.MessageId,
            null,
            UserRole,
            request.Content.Trim(),
            null,
            NodeChatMessageStatusValues.Completed,
            request.CreatedAtUtc,
            request.CreatedAtUtc,
            null,
            null,
            request.MetadataJson,
            cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> CreateAssistantPlaceholderAsync(NodeChatCreateAssistantPlaceholderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty)
        {
            throw new ArgumentException("Assistant placeholders require a non-empty request id.", nameof(request));
        }

        return InsertMessageAsync(request.ConversationId,
            request.MessageId,
            request.RequestId,
            AssistantRole,
            string.Empty,
            null,
            NodeChatMessageStatusValues.Pending,
            request.CreatedAtUtc,
            request.CreatedAtUtc,
            request.Model,
            null,
            request.MetadataJson,
            cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> MarkAssistantStreamingAsync(NodeChatMessageCorrelation correlation, long updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return UpdateCorrelatedMessageAsync(correlation,
            updatedAtUtc,
            NodeChatMessageStatusValues.Streaming,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            true,
            cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> FlushAssistantPartialAsync(NodeChatPartialFlushRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UpdateCorrelatedMessageAsync(request.Correlation,
            request.UpdatedAtUtc,
            null,
            request.Content,
            request.Reasoning,
            null,
            null,
            null,
            null,
            null,
            null,
            request.ReplaceContent,
            cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> TerminalizeAssistantMessageAsync(NodeChatTerminalizeMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsTerminalStatus(request.Status))
        {
            throw new ArgumentException($"Status '{request.Status}' is not terminal.", nameof(request));
        }

        return UpdateCorrelatedMessageAsync(request.Correlation,
            request.UpdatedAtUtc,
            request.Status,
            request.Content,
            request.Reasoning,
            request.Error,
            request.Model,
            request.InputCount,
            request.OutputCount,
            request.TotalCount,
            request.ReasoningCount,
            true,
            cancellationToken);
    }

    public async Task<NodeChatCancelResultDto> CancelMessageAsync(NodeChatCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = await UpdateCorrelatedMessageAsync(request.Correlation,
            request.CancelledAtUtc,
            NodeChatMessageStatusValues.Cancelled,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            true,
            cancellationToken).ConfigureAwait(false);

        return new NodeChatCancelResultDto(request.Correlation, message.Status, true);
    }

    public async Task<NodeChatDeleteResultDto> DeleteConversationAsync(NodeChatDeleteConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                var cancelCount = await dbContext.Database.ExecuteSqlRawAsync("""
                                                                              UPDATE messages
                                                                              SET status = {0}, updated_at_utc = {1}
                                                                              WHERE conversation_id = {2}
                                                                                AND status IN ({3}, {4});
                                                                              """,
                    [NodeChatMessageStatusValues.Cancelled, request.DeletedAtUtc, request.ConversationId, NodeChatMessageStatusValues.Pending, NodeChatMessageStatusValues.Streaming],
                    token).ConfigureAwait(false);

                if (request.PurgeImmediately)
                {
                    await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM messages WHERE conversation_id = {0};", [request.ConversationId], token).ConfigureAwait(false);
                    await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM tool_events WHERE conversation_id = {0};", [request.ConversationId], token).ConfigureAwait(false);
                    await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM purged_tombstones WHERE conversation_id = {0};", [request.ConversationId], token).ConfigureAwait(false);
                    await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM conversations WHERE conversation_id = {0};", [request.ConversationId], token).ConfigureAwait(false);
                }
                else
                {
                    await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET purged = 1, last_seen_utc = {0} WHERE conversation_id = {1};",
                        [request.DeletedAtUtc, request.ConversationId],
                        token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new NodeChatDeleteResultDto(request.ConversationId, cancelCount > 0, request.PurgeImmediately);
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
                                             m.content, m.status
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      WHERE c.purged = 0
                                      ORDER BY c.last_seen_utc DESC
                                      LIMIT $limit;
                                      """;
                return await ReadConversationSummariesAsync(command, request.Limit, token).ConfigureAwait(false);
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
                                             m.content, m.status
                                      FROM conversations c
                                      LEFT JOIN messages m ON m.message_id = (
                                          SELECT mi.message_id FROM messages mi
                                          WHERE mi.conversation_id = c.conversation_id
                                          ORDER BY mi.sequence DESC LIMIT 1)
                                      ORDER BY c.last_seen_utc DESC
                                      LIMIT $limit;
                                      """;
                return await ReadConversationSummariesAsync(command, request.Limit, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<NodeChatPersistedMessageDto> InsertMessageAsync(Guid conversationId,
        Guid messageId,
        Guid? requestId,
        string role,
        string content,
        string? reasoning,
        string status,
        long createdAtUtc,
        long updatedAtUtc,
        string? model,
        string? error,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(conversationId, messageId),
            async (dbContext, token) =>
            {
                var sequence = await NextSequenceAsync(dbContext, conversationId, token).ConfigureAwait(false);
                var metadata = SerializeMetadata(metadataJson, reasoning, model, null, null, null, null);

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error)
                                      VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, $error);
                                      """;
                AddParameter(command, "$message_id", messageId);
                AddParameter(command, "$conversation_id", conversationId);
                AddParameter(command, "$sequence", sequence);
                AddParameter(command, "$role", role);
                AddParameter(command, "$content", Encode(content));
                AddParameter(command, "$metadata_json", metadata);
                AddParameter(command, "$created_at_utc", createdAtUtc);
                AddParameter(command, "$updated_at_utc", updatedAtUtc);
                AddParameter(command, "$status", status);
                AddParameter(command, "$request_id", requestId);
                AddParameter(command, "$error", error);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await TouchConversationAsync(dbContext, conversationId, updatedAtUtc, token).ConfigureAwait(false);

                return new NodeChatPersistedMessageDto(messageId, conversationId, requestId, sequence, role, content, reasoning, status, createdAtUtc, updatedAtUtc, model, error, metadataJson);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<NodeChatPersistedMessageDto> UpdateCorrelatedMessageAsync(NodeChatMessageCorrelation correlation,
        long updatedAtUtc,
        string? status,
        string? content,
        string? reasoning,
        string? error,
        string? model,
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        int? reasoningTokens,
        bool replaceContent,
        CancellationToken cancellationToken)
    {
        ValidateCorrelation(correlation);

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(correlation.ConversationId, correlation.MessageId),
            async (dbContext, token) =>
            {
                var current = await ReadMessageAsync(dbContext, correlation.ConversationId, correlation.MessageId, token).ConfigureAwait(false)
                              ?? throw new InvalidOperationException("The correlated node chat message was not found.");
                if (current.RequestId != correlation.RequestId)
                {
                    throw new InvalidOperationException("The correlated node chat request id did not match the persisted message.");
                }

                var nextContent = ResolveNextContent(current.Content, content, replaceContent);
                var nextReasoning = reasoning ?? current.Reasoning;
                var nextModel = model ?? current.Model;
                var nextStatus = status ?? current.Status;
                var nextError = error ?? current.Error;
                var nextInputTokens = inputTokens ?? current.InputCount;
                var nextOutputTokens = outputTokens ?? current.OutputCount;
                var nextTotalTokens = totalTokens ?? current.TotalCount;
                var nextReasoningTokens = reasoningTokens ?? current.ReasoningCount;
                var metadata = SerializeMetadata(current.MetadataJson, nextReasoning, nextModel, nextInputTokens, nextOutputTokens, nextTotalTokens, nextReasoningTokens);

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      UPDATE messages
                                      SET content = $content, metadata_json = $metadata_json, updated_at_utc = $updated_at_utc, status = $status, error = $error
                                      WHERE conversation_id = $conversation_id
                                        AND message_id = $message_id
                                        AND request_id = $request_id;
                                      """;
                AddParameter(command, "$content", Encode(nextContent));
                AddParameter(command, "$metadata_json", metadata);
                AddParameter(command, "$updated_at_utc", updatedAtUtc);
                AddParameter(command, "$status", nextStatus);
                AddParameter(command, "$error", nextError);
                AddParameter(command, "$conversation_id", correlation.ConversationId);
                AddParameter(command, "$message_id", correlation.MessageId);
                AddParameter(command, "$request_id", correlation.RequestId);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await TouchConversationAsync(dbContext, correlation.ConversationId, updatedAtUtc, token).ConfigureAwait(false);

                return current with
                {
                    Content = nextContent,
                    Reasoning = nextReasoning,
                    Status = nextStatus,
                    UpdatedAtUtc = updatedAtUtc,
                    Model = nextModel,
                    Error = nextError,
                    InputCount = nextInputTokens,
                    OutputCount = nextOutputTokens,
                    TotalCount = nextTotalTokens,
                    ReasoningCount = nextReasoningTokens
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> NextSequenceAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), -1) + 1 FROM messages WHERE conversation_id = $conversation_id;";
        AddParameter(command, "$conversation_id", conversationId);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<NodeChatPersistedMessageDto?> ReadMessageAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false);
        return messages.SingleOrDefault(message => message.MessageId == messageId);
    }

    private static async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ReadMessagesAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT message_id, conversation_id, request_id, sequence, role, content, metadata_json, status, created_at_utc, updated_at_utc, error
                              FROM messages
                              WHERE conversation_id = $conversation_id
                              ORDER BY sequence ASC;
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
                metadata.ReasoningCount));
        }

        return messages;
    }

    private static async Task<IReadOnlyList<NodeChatConversationSummaryDto>> ReadConversationSummariesAsync(DbCommand command, int? limit, CancellationToken cancellationToken)
    {
        AddParameter(command, "$limit", limit is > 0 ? limit.Value : int.MaxValue);

        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var conversations = new List<NodeChatConversationSummaryDto>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var content = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false)
                ? null
                : Decode(await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false));
            conversations.Add(new NodeChatConversationSummaryDto(Guid.Parse(reader.GetString(0)),
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                Preview(content),
                await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(6),
                reader.GetBoolean(4)));
        }

        return conversations;
    }

    private static async Task TouchConversationAsync(NodeChatDbContext dbContext, Guid conversationId, long lastSeenUtc, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET last_seen_utc = {0} WHERE conversation_id = {1};",
            [lastSeenUtc, conversationId],
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateCorrelation(NodeChatMessageCorrelation correlation)
    {
        if (correlation.ConversationId == Guid.Empty || correlation.MessageId == Guid.Empty || correlation.RequestId == Guid.Empty)
        {
            throw new ArgumentException("Conversation, message, and request ids are required for correlated chat persistence operations.", nameof(correlation));
        }
    }

    private static bool IsTerminalStatus(string status)
    {
        return string.Equals(status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal)
               || string.Equals(status, NodeChatMessageStatusValues.Cancelled, StringComparison.Ordinal)
               || string.Equals(status, NodeChatMessageStatusValues.Failed, StringComparison.Ordinal)
               || string.Equals(status, NodeChatMessageStatusValues.Interrupted, StringComparison.Ordinal);
    }

    private static byte[] Encode(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(byte[] value)
    {
        return Encoding.UTF8.GetString(value);
    }

    private static string ResolveNextContent(string currentContent, string? content, bool replaceContent)
    {
        if (content is null)
        {
            return currentContent;
        }

        return replaceContent ? content : currentContent + content;
    }

    private static string? Preview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120];
    }

    private static byte[]? SerializeMetadata(string? metadataJson,
        string? reasoning,
        string? model,
        int? inputTokens,
        int? outputTokens,
        int? totalTokens,
        int? reasoningTokens)
    {
        if (metadataJson is null && reasoning is null && model is null && inputTokens is null && outputTokens is null && totalTokens is null && reasoningTokens is null)
        {
            return null;
        }

        return Encode(JsonSerializer.Serialize(new NodeChatMessageMetadata(metadataJson, reasoning, model, inputTokens, outputTokens, totalTokens, reasoningTokens), MetadataJsonOptions));
    }

    private static NodeChatMessageMetadata DeserializeMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return new NodeChatMessageMetadata(null, null, null, null, null, null, null);
        }

        return JsonSerializer.Deserialize<NodeChatMessageMetadata>(metadataJson, MetadataJsonOptions) ?? new NodeChatMessageMetadata(metadataJson, null, null, null, null, null, null);
    }

    private static async Task OpenIfNeededAsync(DbConnection? connection, CancellationToken cancellationToken)
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

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = DbValue(value);
        command.Parameters.Add(parameter);
    }

    private static object DbValue(object? value)
    {
        return value ?? DBNull.Value;
    }

    private sealed record NodeChatMessageMetadata(
        string? MetadataJson,
        string? Reasoning,
        string? Model,
        int? InputCount,
        int? OutputCount,
        int? TotalCount,
        int? ReasoningCount);
}
