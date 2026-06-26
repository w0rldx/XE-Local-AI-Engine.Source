namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Conversation-lifecycle commands behind <see cref="NodeChatPersistenceService" />: create/ensure/rename/pin/
///     archive/delete plus the conversation-scoped origin and selected-path accessors. Shares the single
///     <see cref="NodeChatPersistenceWriter" /> so the per-conversation write-key serialization is preserved.
/// </summary>
internal sealed class NodeChatConversationCommands(NodeChatPersistenceWriter writer, IConversationUploadedFileStore? uploadedFileStore)
{
    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    // Optional: present in production (DI), absent in the single-arg test compositions that create no uploaded files.
    private readonly IConversationUploadedFileStore? _uploadedFileStore = uploadedFileStore;

    public async Task<NodeChatConversationDto> CreateConversationAsync(NodeChatCreateConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversationId = Guid.NewGuid();
        var createdAtUtc = request.CreatedAtUtc;

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(conversationId),
            async (dbContext, token) =>
            {
                // A new conversation inherits the bound agent's default-temporary-chat flag (adaptive-memory write-only
                // suppression). Read it inline from agent_definitions on the same connection so the seam is self-
                // contained (no store injected into the raw-SQL write path); an unbound conversation defaults to false.
                var memoryExcluded = await ReadAgentDefaultTemporaryChatAsync(dbContext, request.AgentDefinitionId, token).ConfigureAwait(false);

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, agent_definition_id, memory_excluded)
                                      VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0, $origin, $agent_definition_id, $memory_excluded);
                                      """;
                AddParameter(command, "$conversation_id", conversationId);
                AddParameter(command, "$title", EncryptTitle(request.Title, dbContext, conversationId));
                AddParameter(command, "$user_id", request.UserId);
                AddParameter(command, "$created_at_utc", createdAtUtc);
                AddParameter(command, "$last_seen_utc", createdAtUtc);
                AddParameter(command, "$origin", request.Origin);
                AddParameter(command, "$agent_definition_id", request.AgentDefinitionId);
                AddParameter(command, "$memory_excluded", memoryExcluded ? 1 : 0);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return new NodeChatConversationDto(conversationId, request.Title, request.UserId, createdAtUtc, createdAtUtc, Purged: false, [], request.Origin,
                    AgentDefinitionId: request.AgentDefinitionId,
                    MemoryExcluded: memoryExcluded);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the bound agent's <c>default_temporary_chat</c> flag on the supplied connection so a new conversation
    ///     can inherit it. Returns false when there is no binding or the bound definition no longer exists (degrades to
    ///     non-temporary, matching the resolver's deleted-definition fallback). Raw SELECT to keep the create path
    ///     self-contained — no agent store is injected into the serialized raw-SQL writer.
    /// </summary>
    private static async Task<bool> ReadAgentDefaultTemporaryChatAsync(NodeChatDbContext dbContext, Guid? agentDefinitionId, CancellationToken cancellationToken)
    {
        if (agentDefinitionId is not { } definitionId)
        {
            return false;
        }

        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT default_temporary_chat FROM agent_definitions WHERE id = $id;";
        AddParameter(command, "$id", definitionId);
        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        // SQLite stores the bool as 0/1; a missing row returns null → non-temporary.
        return result is not null and not DBNull && Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0L;
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
                AddParameter(command, "$title", EncryptTitle(request.Title, dbContext, request.ConversationId));
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

        var result = await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false);

                var cancelCount = await dbContext.Database.ExecuteSqlRawAsync(sql: """
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
                    await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM conversation_uploaded_files WHERE conversation_id = {0};", [request.ConversationId], token).ConfigureAwait(false);
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

        if (request.PurgeImmediately && _uploadedFileStore is not null)
        {
            // The encrypted upload bytes and cached extracted text live on disk, not in a column, so the FK cascade /
            // raw-SQL row purge above does not touch them. Remove the conversation's on-disk upload directory too.
            await _uploadedFileStore.DeleteAllForConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        }

        return result;
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
                // is required. The title is encrypted before writing; null stays null.
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "UPDATE conversations SET title = $title, last_seen_utc = $last_seen_utc WHERE conversation_id = $conversation_id AND purged = 0;";
                AddParameter(command, "$title", EncryptTitle(title, dbContext, request.ConversationId));
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

    public async Task<NodeChatConversationDto?> SetConversationMemoryExcludedAsync(NodeChatSetConversationMemoryExcludedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForConversation(request.ConversationId),
            async (dbContext, token) =>
            {
                // Plaintext non-nullable bool column → ExecuteSqlRawAsync is sufficient (mirrors SetConversationPinnedAsync).
                var updated = await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET memory_excluded = {0}, last_seen_utc = {1} WHERE conversation_id = {2} AND purged = 0;",
                    [request.MemoryExcluded, request.UpdatedAtUtc, request.ConversationId],
                    token).ConfigureAwait(false);

                return updated == 0 ? null : await ReadConversationWithMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
