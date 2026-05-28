namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Chat;

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
    private static readonly JsonSerializerOptions SelectedPathJsonOptions = new(JsonSerializerDefaults.Web);
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
                                      INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin)
                                      VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0, $origin);
                                      """;
                AddParameter(command, "$conversation_id", conversationId);
                AddParameter(command, "$title", request.Title);
                AddParameter(command, "$user_id", request.UserId);
                AddParameter(command, "$created_at_utc", createdAtUtc);
                AddParameter(command, "$last_seen_utc", createdAtUtc);
                AddParameter(command, "$origin", request.Origin);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return new NodeChatConversationDto(conversationId, request.Title, request.UserId, createdAtUtc, createdAtUtc, false, [], request.Origin);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetConversationOriginAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(conversationId),
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT origin FROM conversations WHERE conversation_id = $conversation_id;";
                AddParameter(command, "$conversation_id", conversationId);

                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return result as string;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>?> GetSelectedPathAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(conversationId),
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT selected_path_json FROM conversations WHERE conversation_id = $conversation_id AND purged = 0;";
                AddParameter(command, "$conversation_id", conversationId);

                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return DeserializeSelectedPath(result as string);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> SetSelectedPathAsync(NodeChatSetSelectedPathRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var selectedPath = request.SelectedPath ?? new Dictionary<Guid, Guid>();

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                // Raw ADO.NET (not ExecuteSqlRawAsync): a cleared selection writes a NULL column, and EF's raw-SQL
                // parameter builder has no store-type mapping for DBNull, so a typed DbParameter via AddParameter
                // is required.
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "UPDATE conversations SET selected_path_json = $selected_path_json, last_seen_utc = $last_seen_utc WHERE conversation_id = $conversation_id AND purged = 0;";
                AddParameter(command, "$selected_path_json", SerializeSelectedPath(selectedPath));
                AddParameter(command, "$last_seen_utc", request.UpdatedAtUtc);
                AddParameter(command, "$conversation_id", request.ConversationId);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return (IReadOnlyDictionary<Guid, Guid>)new Dictionary<Guid, Guid>(selectedPath);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatConversationDto> EnsureConversationAsync(NodeChatEnsureConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConversationId == Guid.Empty)
        {
            throw new ArgumentException("EnsureConversationAsync requires a non-empty conversation id.", nameof(request));
        }

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                // Read the existing row regardless of purged state: an already-purged row still occupies the id,
                // so we must NOT attempt to recreate it. INSERT OR IGNORE makes the insert race-safe against the
                // serialized remote dispatch path.
                var existing = await ReadConversationRowAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing with
                    {
                        Messages = await ReadMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false)
                    };
                }

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT OR IGNORE INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin)
                                      VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0, $origin);
                                      """;
                AddParameter(command, "$conversation_id", request.ConversationId);
                AddParameter(command, "$title", request.Title);
                AddParameter(command, "$user_id", request.UserId);
                AddParameter(command, "$created_at_utc", request.CreatedAtUtc);
                AddParameter(command, "$last_seen_utc", request.CreatedAtUtc);
                AddParameter(command, "$origin", request.Origin);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                // Re-read so a concurrent insert that won the race (IGNORE) still returns the authoritative row.
                var ensured = await ReadConversationRowAsync(dbContext, request.ConversationId, token).ConfigureAwait(false)
                              ?? throw new InvalidOperationException("The node chat conversation could not be ensured.");
                return ensured with
                {
                    Messages = await ReadMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false)
                };
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
                                                  SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, selected_path_json
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
                    await ReadMessagesAsync(dbContext, conversationId, token).ConfigureAwait(false),
                    conversationReader.GetString(6),
                    conversationReader.GetBoolean(7),
                    conversationReader.GetBoolean(8),
                    await conversationReader.IsDBNullAsync(9, token).ConfigureAwait(false) ? null : Guid.Parse(conversationReader.GetString(9)),
                    DeserializeSelectedPath(await conversationReader.IsDBNullAsync(10, token).ConfigureAwait(false) ? null : conversationReader.GetString(10)));

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
            request.Origin,
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
            request.Origin,
            cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> MarkAssistantQueuedAsync(NodeChatMessageCorrelation correlation, long updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return UpdateCorrelatedMessageAsync(correlation,
            updatedAtUtc,
            NodeChatMessageStatusValues.Queued,
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
                                                                                AND status IN ({3}, {4}, {5});
                                                                              """,
                    [NodeChatMessageStatusValues.Cancelled, request.DeletedAtUtc, request.ConversationId, NodeChatMessageStatusValues.Pending, NodeChatMessageStatusValues.Queued, NodeChatMessageStatusValues.Streaming],
                    token).ConfigureAwait(false);

                if (request.PurgeImmediately)
                {
                    // ON DELETE CASCADE is NOT enforced on the node-sqlite connection (no PRAGMA foreign_keys=ON),
                    // so every child table must be purged explicitly or plaintext rows orphan (privacy gap).
                    await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM message_feedback WHERE conversation_id = {0};", [request.ConversationId], token).ConfigureAwait(false);
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

    public async Task<NodeChatConversationDto?> RenameConversationAsync(NodeChatRenameConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                // Raw ADO.NET (not ExecuteSqlRawAsync): a cleared title writes a NULL column, and EF's raw-SQL
                // parameter builder has no store-type mapping for DBNull, so a typed DbParameter via AddParameter
                // is required.
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "UPDATE conversations SET title = $title, last_seen_utc = $last_seen_utc WHERE conversation_id = $conversation_id AND purged = 0;";
                AddParameter(command, "$title", title);
                AddParameter(command, "$last_seen_utc", request.UpdatedAtUtc);
                AddParameter(command, "$conversation_id", request.ConversationId);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                var updated = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return updated == 0 ? null : await ReadConversationWithMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatConversationDto?> SetConversationPinnedAsync(NodeChatSetConversationPinnedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                var updated = await dbContext.Database.ExecuteSqlRawAsync(
                    "UPDATE conversations SET is_pinned = {0}, last_seen_utc = {1} WHERE conversation_id = {2} AND purged = 0;",
                    [request.IsPinned, request.UpdatedAtUtc, request.ConversationId],
                    token).ConfigureAwait(false);

                return updated == 0 ? null : await ReadConversationWithMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatConversationDto?> SetConversationArchivedAsync(NodeChatSetConversationArchivedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                var updated = await dbContext.Database.ExecuteSqlRawAsync(
                    "UPDATE conversations SET archived = {0}, last_seen_utc = {1} WHERE conversation_id = {2} AND purged = 0;",
                    [request.Archived, request.UpdatedAtUtc, request.ConversationId],
                    token).ConfigureAwait(false);

                return updated == 0 ? null : await ReadConversationWithMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatBranchResultDto?> BranchConversationAsync(NodeChatBranchConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read the source (including its messages) on its own write key, then create the branch under the new
        // conversation's write key. Two serialized scopes avoid a cross-conversation lock-ordering hazard.
        var source = await GetConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var cutoff = source.Messages.FirstOrDefault(message => message.MessageId == request.MessageId);
        if (cutoff is null)
        {
            return null;
        }

        // Collapse variant groups so the branch is a LINEAR thread: the branched-from turn contributes only the
        // chosen revision (the cutoff message), and every other variant group contributes only its newest member
        // (matching the client's default-active = newest revision). Non-variant messages always pass through.
        // Without this, branching from a revision copies the sibling variants too, and since variant_group_id is
        // dropped on copy (below) they render as duplicate stacked assistant turns instead of one. (RC fix.)
        var eligible = source.Messages.Where(message => message.Sequence <= cutoff.Sequence).ToArray();
        var newestSequenceByGroup = eligible
                                    .Where(message => message.VariantGroupId is not null && message.VariantGroupId != cutoff.VariantGroupId)
                                    .GroupBy(message => message.VariantGroupId!.Value)
                                    .ToDictionary(group => group.Key, group => group.Max(message => message.Sequence));

        var copies = eligible
                     .Where(message => message.VariantGroupId is null
                                       || message.MessageId == cutoff.MessageId
                                       || (message.VariantGroupId != cutoff.VariantGroupId
                                           && newestSequenceByGroup.TryGetValue(message.VariantGroupId.Value, out var newestSequence)
                                           && message.Sequence == newestSequence))
                     .OrderBy(message => message.Sequence)
                     .ToArray();
        var branchedConversationId = Guid.NewGuid();

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(branchedConversationId),
            async (dbContext, token) =>
            {
                await using var conversationCommand = dbContext.Database.GetDbConnection().CreateCommand();
                conversationCommand.CommandText = """
                                                  INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id)
                                                  VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0, $origin, 0, 0, $branch_of_conversation_id);
                                                  """;
                AddParameter(conversationCommand, "$conversation_id", branchedConversationId);
                AddParameter(conversationCommand, "$title", source.Title);
                AddParameter(conversationCommand, "$user_id", source.UserId);
                AddParameter(conversationCommand, "$created_at_utc", request.CreatedAtUtc);
                AddParameter(conversationCommand, "$last_seen_utc", request.CreatedAtUtc);
                // A branch is always a fresh node-local conversation, even when branched from a remote mirror.
                AddParameter(conversationCommand, "$origin", NodeChatOriginValues.Local);
                AddParameter(conversationCommand, "$branch_of_conversation_id", request.ConversationId);
                await OpenIfNeededAsync(conversationCommand.Connection, token).ConfigureAwait(false);
                await conversationCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                foreach (var message in copies)
                {
                    await using var messageCommand = dbContext.Database.GetDbConnection().CreateCommand();
                    messageCommand.CommandText = """
                                                 INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id)
                                                 VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, $error, $origin, $parent_message_id, $variant_group_id);
                                                 """;
                    AddParameter(messageCommand, "$message_id", Guid.NewGuid());
                    AddParameter(messageCommand, "$conversation_id", branchedConversationId);
                    AddParameter(messageCommand, "$sequence", message.Sequence);
                    AddParameter(messageCommand, "$role", message.Role);
                    AddParameter(messageCommand, "$content", Encode(message.Content));
                    AddParameter(messageCommand, "$metadata_json", SerializeMetadata(message.MetadataJson, message.Reasoning, message.Model, message.InputCount, message.OutputCount, message.TotalCount, message.ReasoningCount));
                    AddParameter(messageCommand, "$created_at_utc", message.CreatedAtUtc);
                    AddParameter(messageCommand, "$updated_at_utc", message.UpdatedAtUtc);
                    AddParameter(messageCommand, "$status", message.Status);
                    AddParameter(messageCommand, "$request_id", message.RequestId);
                    AddParameter(messageCommand, "$error", message.Error);
                    AddParameter(messageCommand, "$origin", NodeChatOriginValues.Local);
                    // Branch copies are a fresh linear thread; provenance is on the conversation, not per message.
                    AddParameter(messageCommand, "$parent_message_id", null);
                    AddParameter(messageCommand, "$variant_group_id", null);
                    await OpenIfNeededAsync(messageCommand.Connection, token).ConfigureAwait(false);
                    await messageCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                return new NodeChatBranchResultDto(request.ConversationId, branchedConversationId, copies.Length);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatMessageVariantDto?> CreateMessageVariantAsync(NodeChatCreateMessageVariantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.NewMessageId == Guid.Empty || request.RequestId == Guid.Empty)
        {
            throw new ArgumentException("Variant messages require non-empty message and request ids.", nameof(request));
        }

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(request.ConversationId, request.NewMessageId),
            async (dbContext, token) =>
            {
                var original = await ReadMessageAsync(dbContext, request.ConversationId, request.OriginalMessageId, token).ConfigureAwait(false);
                if (original is null)
                {
                    return null;
                }

                // The whole turn shares a variant group; mint one and back-stamp the original when it has none.
                var variantGroupId = original.VariantGroupId ?? Guid.NewGuid();
                if (original.VariantGroupId is null)
                {
                    await using var stampCommand = dbContext.Database.GetDbConnection().CreateCommand();
                    stampCommand.CommandText = "UPDATE messages SET variant_group_id = $variant_group_id WHERE conversation_id = $conversation_id AND message_id = $message_id;";
                    AddParameter(stampCommand, "$variant_group_id", variantGroupId);
                    AddParameter(stampCommand, "$conversation_id", request.ConversationId);
                    AddParameter(stampCommand, "$message_id", request.OriginalMessageId);
                    await OpenIfNeededAsync(stampCommand.Connection, token).ConfigureAwait(false);
                    await stampCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                // The new sibling variant is an assistant placeholder: same parent (the user turn), shared group.
                var sequence = await NextSequenceAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
                var metadata = SerializeMetadata(request.MetadataJson, null, request.Model, null, null, null, null);

                await using var insertCommand = dbContext.Database.GetDbConnection().CreateCommand();
                insertCommand.CommandText = """
                                            INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id)
                                            VALUES ($message_id, $conversation_id, $sequence, $role, '', $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, NULL, $origin, $parent_message_id, $variant_group_id);
                                            """;
                AddParameter(insertCommand, "$message_id", request.NewMessageId);
                AddParameter(insertCommand, "$conversation_id", request.ConversationId);
                AddParameter(insertCommand, "$sequence", sequence);
                AddParameter(insertCommand, "$role", AssistantRole);
                AddParameter(insertCommand, "$metadata_json", metadata);
                AddParameter(insertCommand, "$created_at_utc", request.CreatedAtUtc);
                AddParameter(insertCommand, "$updated_at_utc", request.CreatedAtUtc);
                AddParameter(insertCommand, "$status", NodeChatMessageStatusValues.Pending);
                AddParameter(insertCommand, "$request_id", request.RequestId);
                AddParameter(insertCommand, "$origin", NodeChatOriginValues.Local);
                AddParameter(insertCommand, "$parent_message_id", request.OriginalMessageId);
                AddParameter(insertCommand, "$variant_group_id", variantGroupId);
                await OpenIfNeededAsync(insertCommand.Connection, token).ConfigureAwait(false);
                await insertCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await TouchConversationAsync(dbContext, request.ConversationId, request.CreatedAtUtc, token).ConfigureAwait(false);

                var variant = new NodeChatPersistedMessageDto(request.NewMessageId,
                    request.ConversationId,
                    request.RequestId,
                    sequence,
                    AssistantRole,
                    string.Empty,
                    null,
                    NodeChatMessageStatusValues.Pending,
                    request.CreatedAtUtc,
                    request.CreatedAtUtc,
                    request.Model,
                    null,
                    request.MetadataJson,
                    Origin: NodeChatOriginValues.Local,
                    ParentMessageId: request.OriginalMessageId,
                    VariantGroupId: variantGroupId);

                return new NodeChatMessageVariantDto(variantGroupId, request.OriginalMessageId, variant);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ListMessageVariantsAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(conversationId),
            async (dbContext, token) =>
            {
                var messages = await ReadMessagesAsync(dbContext, conversationId, token).ConfigureAwait(false);
                var anchor = messages.SingleOrDefault(message => message.MessageId == messageId);
                if (anchor is null)
                {
                    return (IReadOnlyList<NodeChatPersistedMessageDto>)[];
                }

                // No variant group yet → the message is its own sole variant.
                if (anchor.VariantGroupId is null)
                {
                    return [anchor];
                }

                return messages.Where(message => message.VariantGroupId == anchor.VariantGroupId)
                               .OrderBy(message => message.Sequence)
                               .ToArray();
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatMessageFeedbackDto> SetMessageFeedbackAsync(NodeChatSetMessageFeedbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!NodeChatFeedbackRatingValues.All.Contains(request.Rating))
        {
            throw new ArgumentException($"Rating '{request.Rating}' is not a recognized feedback rating.", nameof(request));
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(request.ConversationId, request.MessageId),
            async (dbContext, token) =>
            {
                // Upsert keyed on the message id: re-submitting feedback overwrites rating/comment but preserves the
                // first-seen created_at_utc.
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO message_feedback (message_id, conversation_id, rating, comment, created_at_utc, updated_at_utc)
                                      VALUES ($message_id, $conversation_id, $rating, $comment, $created_at_utc, $updated_at_utc)
                                      ON CONFLICT(message_id) DO UPDATE SET
                                          conversation_id = excluded.conversation_id,
                                          rating = excluded.rating,
                                          comment = excluded.comment,
                                          updated_at_utc = excluded.updated_at_utc;
                                      """;
                AddParameter(command, "$message_id", request.MessageId);
                AddParameter(command, "$conversation_id", request.ConversationId);
                AddParameter(command, "$rating", request.Rating);
                AddParameter(command, "$comment", comment);
                AddParameter(command, "$created_at_utc", request.UpdatedAtUtc);
                AddParameter(command, "$updated_at_utc", request.UpdatedAtUtc);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return await ReadFeedbackAsync(dbContext, request.ConversationId, request.MessageId, token).ConfigureAwait(false)
                       ?? throw new InvalidOperationException("The message feedback row could not be persisted.");
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatMessageFeedbackDto?> GetMessageFeedbackAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(conversationId, messageId),
            (dbContext, token) => ReadFeedbackAsync(dbContext, conversationId, messageId, token),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NodeChatMessageFeedbackDto?> ReadFeedbackAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
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

    private static async Task<NodeChatConversationDto?> ReadConversationWithMessagesAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await ReadConversationRowAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false);
        return conversation is null
            ? null
            : conversation with { Messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false) };
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
        string origin,
        CancellationToken cancellationToken,
        Guid? parentMessageId = null,
        Guid? variantGroupId = null)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(conversationId, messageId),
            async (dbContext, token) =>
            {
                var sequence = await NextSequenceAsync(dbContext, conversationId, token).ConfigureAwait(false);
                var metadata = SerializeMetadata(metadataJson, reasoning, model, null, null, null, null);

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id)
                                      VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, $error, $origin, $parent_message_id, $variant_group_id);
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
                AddParameter(command, "$origin", origin);
                AddParameter(command, "$parent_message_id", parentMessageId);
                AddParameter(command, "$variant_group_id", variantGroupId);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await TouchConversationAsync(dbContext, conversationId, updatedAtUtc, token).ConfigureAwait(false);

                return new NodeChatPersistedMessageDto(messageId, conversationId, requestId, sequence, role, content, reasoning, status, createdAtUtc, updatedAtUtc, model, error, metadataJson, Origin: origin, ParentMessageId: parentMessageId, VariantGroupId: variantGroupId);
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

    private static async Task<NodeChatConversationDto?> ReadConversationRowAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id
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

        return new NodeChatConversationDto(Guid.Parse(reader.GetString(0)),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetBoolean(5),
            [],
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : Guid.Parse(reader.GetString(9)));
    }

    private static async Task<NodeChatPersistedMessageDto?> ReadMessageAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        var messages = await ReadMessagesAsync(dbContext, conversationId, cancellationToken).ConfigureAwait(false);
        return messages.SingleOrDefault(message => message.MessageId == messageId);
    }

    private static async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ReadMessagesAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
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
                await reader.IsDBNullAsync(15, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(15)));
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
                reader.GetBoolean(4),
                reader.GetString(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9)));
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

    private static string? SerializeSelectedPath(IReadOnlyDictionary<Guid, Guid> selectedPath)
    {
        if (selectedPath.Count == 0)
        {
            return null;
        }

        // String keys/values keep the JSON object portable: the same {variantGroupId->selectedMessageId} map can be
        // parsed by any platform without depending on a Guid dictionary-key converter.
        var serializable = selectedPath.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.ToString());
        return JsonSerializer.Serialize(serializable, SelectedPathJsonOptions);
    }

    private static IReadOnlyDictionary<Guid, Guid>? DeserializeSelectedPath(string? selectedPathJson)
    {
        if (string.IsNullOrWhiteSpace(selectedPathJson))
        {
            return null;
        }

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(selectedPathJson, SelectedPathJsonOptions);
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        var parsed = new Dictionary<Guid, Guid>(raw.Count);
        foreach (var pair in raw)
        {
            if (Guid.TryParse(pair.Key, out var variantGroupId) && Guid.TryParse(pair.Value, out var selectedMessageId))
            {
                parsed[variantGroupId] = selectedMessageId;
            }
        }

        return parsed.Count == 0 ? null : parsed;
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
