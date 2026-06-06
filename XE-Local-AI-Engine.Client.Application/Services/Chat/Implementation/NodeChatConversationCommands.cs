namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using static XE_Local_AI_Engine.Client.Services.Chat.Implementation.NodeChatMetadataSerializer;
using static XE_Local_AI_Engine.Client.Services.Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Conversation-lifecycle commands behind <see cref="NodeChatPersistenceService" />: create/ensure/rename/pin/
///     archive/delete plus the conversation-scoped origin and selected-path accessors. Shares the single
///     <see cref="NodeChatPersistenceWriter" /> so the per-conversation write-key serialization is preserved.
/// </summary>
internal sealed class NodeChatConversationCommands(NodeChatPersistenceWriter writer)
{
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
}
