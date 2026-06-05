namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;

public sealed partial class NodeChatPersistenceService(NodeChatPersistenceWriter writer) : INodeChatPersistenceService
{
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

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
                                      INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, agent_definition_id)
                                      VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0, $origin, $agent_definition_id);
                                      """;
                AddParameter(command, "$conversation_id", conversationId);
                AddParameter(command, "$title", request.Title);
                AddParameter(command, "$user_id", request.UserId);
                AddParameter(command, "$created_at_utc", createdAtUtc);
                AddParameter(command, "$last_seen_utc", createdAtUtc);
                AddParameter(command, "$origin", request.Origin);
                AddParameter(command, "$agent_definition_id", request.AgentDefinitionId);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return new NodeChatConversationDto(conversationId, request.Title, request.UserId, createdAtUtc, createdAtUtc, false, [], request.Origin, AgentDefinitionId: request.AgentDefinitionId);
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
                                                  SELECT conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id, selected_path_json, agent_definition_id
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
                    DeserializeSelectedPath(await conversationReader.IsDBNullAsync(10, token).ConfigureAwait(false) ? null : conversationReader.GetString(10)),
                    await conversationReader.IsDBNullAsync(11, token).ConfigureAwait(false) ? null : Guid.Parse(conversationReader.GetString(11)));

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
            cancellationToken,
            agentDefinitionId: request.AgentDefinitionId,
            agentName: request.AgentName,
            reasoningEffort: request.ReasoningEffort);
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
            cancellationToken,
            request.Parts,
            request.GenerationDurationMs);
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
                    [
                        NodeChatMessageStatusValues.Cancelled, request.DeletedAtUtc, request.ConversationId, NodeChatMessageStatusValues.Pending, NodeChatMessageStatusValues.Queued,
                        NodeChatMessageStatusValues.Streaming
                    ],
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
                var updated = await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET is_pinned = {0}, last_seen_utc = {1} WHERE conversation_id = {2} AND purged = 0;",
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
                var updated = await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET archived = {0}, last_seen_utc = {1} WHERE conversation_id = {2} AND purged = 0;",
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
                    AddParameter(messageCommand, "$metadata_json",
                        SerializeMetadata(message.MetadataJson, message.Reasoning, message.Model, message.InputCount, message.OutputCount, message.TotalCount, message.ReasoningCount, message.Parts,
                            message.AgentDefinitionId, message.AgentName, message.ReasoningEffort));
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

                // The new sibling variant is an assistant placeholder: same parent (the user turn), shared group. The
                // per-response agent attribution is stamped at mint time so the pending variant already carries the
                // agent name (symmetric with the send placeholder).
                var sequence = await NextSequenceAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
                var metadata = SerializeMetadata(request.MetadataJson, null, request.Model, null, null, null, null, null, request.AgentDefinitionId, request.AgentName, request.ReasoningEffort);

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
                    VariantGroupId: variantGroupId,
                    AgentDefinitionId: request.AgentDefinitionId,
                    AgentName: request.AgentName,
                    ReasoningEffort: request.ReasoningEffort);

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

}
