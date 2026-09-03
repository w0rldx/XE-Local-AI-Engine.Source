namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Conversation-lifecycle commands behind <see cref="NodeChatPersistenceService" />: create/ensure/rename/pin/
///     archive/delete plus the conversation-scoped origin and selected-path accessors. Shares the single
///     <see cref="NodeChatPersistenceWriter" /> so the per-conversation write-key serialization is preserved.
/// </summary>
internal sealed class NodeChatConversationCommands(
    NodeChatPersistenceWriter writer,
    IConversationUploadedFileStore? uploadedFileStore,
    IWorkSessionArtifactBlobStore? workSessionArtifactBlobStore)
{
    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    // Optional: present in production (DI), absent in the single-arg test compositions that create no uploaded files.
    private readonly IConversationUploadedFileStore? _uploadedFileStore = uploadedFileStore;

    // Optional for the same reason: a test composition that owns no work session has no artifact bytes to tear down.
    private readonly IWorkSessionArtifactBlobStore? _workSessionArtifactBlobStore = workSessionArtifactBlobStore;

    public async Task<NodeChatConversationDto> CreateConversationAsync(NodeChatCreateConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var conversationId = request.ConversationId ?? Guid.NewGuid();
        var createdAtUtc = request.CreatedAtUtc;

        return await _writer.ExecuteConversationExclusiveAsync(conversationId,
            async (dbContext, token) =>
            {
                // A new conversation inherits the bound agent's default-temporary-chat flag (adaptive-memory write-only
                // suppression). Read it inline from agent_definitions on the same connection so the seam is self-
                // contained (no store injected into the raw-SQL write path); an unbound conversation defaults to false.
                var memoryExcluded = await ReadAgentDefaultTemporaryChatAsync(dbContext, request.AgentDefinitionId, token).ConfigureAwait(false);

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, agent_definition_id, memory_excluded, kind)
                                      VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0, $origin, $agent_definition_id, $memory_excluded, $kind);
                                      """;
                AddParameter(command, "$conversation_id", conversationId);
                AddParameter(command, "$title", EncryptTitle(request.Title, dbContext, conversationId));
                AddParameter(command, "$user_id", request.UserId);
                AddParameter(command, "$created_at_utc", createdAtUtc);
                AddParameter(command, "$last_seen_utc", createdAtUtc);
                AddParameter(command, "$origin", request.Origin);
                AddParameter(command, "$agent_definition_id", request.AgentDefinitionId);
                AddParameter(command, "$memory_excluded", memoryExcluded ? 1 : 0);
                AddParameter(command, "$kind", request.Kind);
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

        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
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
        return await _writer.ExecuteConversationSharedAsync(conversationId,
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
        return await _writer.ExecuteConversationSharedAsync(conversationId,
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

        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
            async (dbContext, token) =>
            {
                // Raw ADO.NET (not ExecuteSqlRawAsync): a cleared selection writes a NULL column, and EF's raw-SQL
                // parameter builder has no store-type mapping for DBNull, so a typed DbParameter via AddParameter
                // is required.
                // Changing the selected variant path invalidates any compaction synopsis: the synopsis was built from the
                // previously-selected path and covers messages up to a sequence, so a re-selection inside that covered
                // range would otherwise be misrepresented by stale summary text. Clear it (literal NULLs) so the next send
                // uses full history until the user re-compacts.
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText =
                    "UPDATE conversations SET selected_path_json = $selected_path_json, last_seen_utc = $last_seen_utc, compaction_summary = NULL, compaction_summary_covers_to_sequence = NULL, compaction_summary_updated_at_utc = NULL WHERE conversation_id = $conversation_id AND purged = 0;";
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

        // Captured inside the transaction, used after it: the artifact blobs are keyed by session id and the only
        // record of the conversation → session mapping is the row the purge below deletes.
        Guid? workSessionId = null;

        var result = await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
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
                    // Read the owned session id BEFORE the rows go; nothing can resolve it afterwards.
                    workSessionId = await ReadWorkSessionIdAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);

                    // Delete the complete DB footprint through the shared helper so this path and the retention sweeper
                    // never drift on which child tables constitute a conversation. On-disk upload blobs are torn down
                    // after commit below.
                    await ConversationFootprintPurge.DeleteAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
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

        if (workSessionId is { } sessionId)
        {
            // Same shape as the uploads above: the artifact bytes live on disk under the session id, so the row purge
            // cannot reach them. Best-effort and never throws on a missing directory.
            _workSessionArtifactBlobStore?.DeleteSession(sessionId);
        }

        return result;
    }

    /// <summary>
    ///     The id of the work session this conversation owns (1:1), or null when it owns none. Read on the writer's own
    ///     connection rather than through <c>IAgentWorkSessionStore</c>: that store is scoped and this collaborator
    ///     hangs off a singleton facade, so injecting it would be a captive dependency.
    /// </summary>
    private static async Task<Guid?> ReadWorkSessionIdAsync(NodeChatDbContext dbContext, Guid conversationId, CancellationToken cancellationToken)
    {
        return await dbContext.AgentWorkSessions.AsNoTracking()
                              .Where(entity => entity.ConversationId == conversationId)
                              .Select(entity => (Guid?)entity.Id)
                              .SingleOrDefaultAsync(cancellationToken)
                              .ConfigureAwait(false);
    }

    public async Task<NodeChatConversationDto?> RenameConversationAsync(NodeChatRenameConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();

        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
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

        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
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

        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
            async (dbContext, token) =>
            {
                var updated = await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET archived = {0}, last_seen_utc = {1} WHERE conversation_id = {2} AND purged = 0;",
                    [request.Archived, request.UpdatedAtUtc, request.ConversationId],
                    token).ConfigureAwait(false);

                return updated == 0 ? null : await ReadConversationWithMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatConversationDto?> SetCompactionSummaryAsync(NodeChatSetCompactionSummaryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var summary = string.IsNullOrWhiteSpace(request.Summary) ? null : request.Summary;

        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
            async (dbContext, token) =>
            {
                // Raw ADO.NET (not ExecuteSqlRawAsync): the encrypted summary blob and the nullable covered-sequence both
                // write NULL when cleared, and EF's raw-SQL parameter builder has no store-type mapping for DBNull, so
                // typed DbParameters via AddParameter are required. The summary is encrypted before writing; null stays null.
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText =
                    "UPDATE conversations SET compaction_summary = $summary, compaction_summary_covers_to_sequence = $covers_to, compaction_summary_updated_at_utc = $updated_at, last_seen_utc = $last_seen_utc WHERE conversation_id = $conversation_id AND purged = 0;";
                AddParameter(command, "$summary", dbContext.EncryptConversationCompactionSummary(summary, request.ConversationId));
                AddParameter(command, "$covers_to", summary is null ? null : request.CoversToSequence);
                AddParameter(command, "$updated_at", summary is null ? null : request.UpdatedAtUtc);
                AddParameter(command, "$last_seen_utc", request.UpdatedAtUtc);
                AddParameter(command, "$conversation_id", request.ConversationId);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                var updated = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                return updated == 0 ? null : await ReadConversationWithMessagesAsync(dbContext, request.ConversationId, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeChatConversationDto?> SetConversationMemoryExcludedAsync(NodeChatSetConversationMemoryExcludedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
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
