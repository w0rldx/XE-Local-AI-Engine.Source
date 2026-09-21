namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Variant and branch commands behind <see cref="NodeChatPersistenceService" />: recording a regenerated turn as
///     a sibling variant, listing a turn's variants, and branching a conversation into a new local thread.
/// </summary>
/// <remarks>
///     It reads the branch source via <see cref="NodeChatReadModel" /> on its own write key before writing under the
///     new conversation's key; two serialized scopes avoid a cross-conversation lock-ordering hazard.
/// </remarks>
internal sealed class NodeChatVariantBranchService
{
    private const string AssistantRole = "assistant";

    private readonly NodeChatReadModel _readModel;
    private readonly NodeChatPersistenceWriter _writer;

    public NodeChatVariantBranchService(NodeChatPersistenceWriter writer, NodeChatReadModel readModel)
    {
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(writer);
        _readModel = readModel;
        _writer = writer;
    }

    public async Task<NodeChatBranchResultDto?> BranchConversationAsync(NodeChatBranchConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read the source (including its messages) on its own write key, then create the branch under the new
        // conversation's write key. Two serialized scopes avoid a cross-conversation lock-ordering hazard.
        var source = await _readModel.GetConversationAsync(request.ConversationId, cancellationToken);
        if (source is null)
        {
            return null;
        }

        var cutoff = source.Messages.FirstOrDefault(message => message.MessageId == request.MessageId);
        if (cutoff is null)
        {
            return null;
        }

        // Collapse variant groups so the branch is a LINEAR thread: one revision per group at the group's ANCHOR, which
        // branches a late regeneration of an early turn early. See docs/wiki/05-chat.md, "Branching a conversation".
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(source.Messages);

        // The cutoff's anchored position defines how far the branch reaches: a group participates only if its anchor is
        // at or upstream of it, and a cutoff that is itself a late sibling resolves to the position it renders at.
        var cutoffAnchor = anchorSequence(cutoff);
        var eligible = source.Messages.Where(message => anchorSequence(message) <= cutoffAnchor).ToArray();
        var selection = BuildValidatedSelection(request.SelectedRevisions, source.Messages, cutoff, anchorSequence, cutoffAnchor);
        // The resolver orders by each chosen sibling's own sequence; re-order by anchor so a late-created sibling lands
        // at its group's position instead of the tail. Each copy is also stamped with the anchor sequence below.
        IReadOnlyList<NodeChatPersistedMessageDto> copies =
            SelectedPathResolver.Resolve(eligible, selection).OrderBy(anchorSequence).ToArray();
        var branchedConversationId = Guid.NewGuid();

        return await _writer.ExecuteConversationExclusiveAsync(branchedConversationId,
            async (dbContext, token) =>
            {
                // One transaction around the whole branch, conversation insert and every message copy: the copies are
                // separate INSERTs, so a failure mid-loop would otherwise leave a visible half-copied branch.
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
                var dbTransaction = transaction.GetDbTransaction();

                await using var conversationCommand = dbContext.Database.GetDbConnection().CreateCommand();
                conversationCommand.Transaction = dbTransaction;
                conversationCommand.CommandText = """
                                                  INSERT INTO conversations (conversation_id, title, user_id, created_at_utc, last_seen_utc, purged, origin, is_pinned, archived, branch_of_conversation_id)
                                                  VALUES ($conversation_id, $title, $user_id, $created_at_utc, $last_seen_utc, 0, $origin, 0, 0, $branch_of_conversation_id);
                                                  """;
                AddParameter(conversationCommand, "$conversation_id", branchedConversationId);
                AddParameter(conversationCommand, "$title", EncryptTitle(source.Title, dbContext, branchedConversationId));
                AddParameter(conversationCommand, "$user_id", source.UserId);
                AddParameter(conversationCommand, "$created_at_utc", request.CreatedAtUtc);
                AddParameter(conversationCommand, "$last_seen_utc", request.CreatedAtUtc);
                // A branch is always a fresh node-local conversation, even when branched from a remote mirror.
                AddParameter(conversationCommand, "$origin", NodeChatOriginValues.Local);
                AddParameter(conversationCommand, "$branch_of_conversation_id", request.ConversationId);
                await OpenIfNeededAsync(conversationCommand.Connection, token);
                await conversationCommand.ExecuteNonQueryAsync(token);

                foreach (var message in copies)
                {
                    await using var messageCommand = dbContext.Database.GetDbConnection().CreateCommand();
                    messageCommand.Transaction = dbTransaction;
                    messageCommand.CommandText = """
                                                 INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id)
                                                 VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, $error, $origin, $parent_message_id, $variant_group_id);
                                                 """;
                    // A branch copy is a new row with a fresh message id, so its envelope AAD binds the new
                    // (conversation, message) pair and the already-decrypted content is re-encrypted under it.
                    var copyMessageId = Guid.NewGuid();
                    AddParameter(messageCommand, "$message_id", copyMessageId);
                    AddParameter(messageCommand, "$conversation_id", branchedConversationId);
                    // Stamped at the group's anchored position, not the chosen sibling's own sequence, so the new linear
                    // thread is ordered exactly as the operator saw it. Anchor sequences are unique per source row.
                    AddParameter(messageCommand, "$sequence", anchorSequence(message));
                    AddParameter(messageCommand, "$role", message.Role);
                    AddParameter(messageCommand, "$content", dbContext.EncryptMessageContent(message.Content, branchedConversationId, copyMessageId));
                    AddParameter(messageCommand, "$metadata_json",
                        dbContext.EncryptMessageMetadata(SerializeMetadata(message.MetadataJson, message.Reasoning, message.Model, message.InputCount, message.OutputCount, message.TotalCount,
                                message.ReasoningCount,
                                message.Parts, message.AgentDefinitionId, message.AgentName, message.ReasoningEffort, sources: message.Sources),
                            branchedConversationId,
                            copyMessageId));
                    AddParameter(messageCommand, "$created_at_utc", message.CreatedAtUtc);
                    AddParameter(messageCommand, "$updated_at_utc", message.UpdatedAtUtc);
                    AddParameter(messageCommand, "$status", message.Status);
                    AddParameter(messageCommand, "$request_id", message.RequestId);
                    AddParameter(messageCommand, "$error", message.Error);
                    AddParameter(messageCommand, "$origin", NodeChatOriginValues.Local);
                    // Branch copies are a fresh linear thread; provenance is on the conversation, not per message.
                    AddParameter(messageCommand, "$parent_message_id", value: null);
                    AddParameter(messageCommand, "$variant_group_id", value: null);
                    await OpenIfNeededAsync(messageCommand.Connection, token);
                    await messageCommand.ExecuteNonQueryAsync(token);
                }

                await transaction.CommitAsync(token);
                return new NodeChatBranchResultDto { SourceConversationId = request.ConversationId, BranchedConversationId = branchedConversationId, CopiedMessageCount = copies.Count };
            },
            cancellationToken);
    }

    // Validates the caller's selected-revision map and keeps only the entries whose group ANCHOR — never the selected
    // message's own sequence — is upstream of the cutoff. It fails CLOSED on an integrity violation.
    private static IReadOnlyDictionary<Guid, Guid>? BuildValidatedSelection(IReadOnlyDictionary<Guid, Guid>? requested,
        IReadOnlyList<NodeChatPersistedMessageDto> allMessages,
        NodeChatPersistedMessageDto cutoff,
        Func<NodeChatPersistedMessageDto, int> anchorSequence,
        int cutoffAnchor)
    {
        var selection = new Dictionary<Guid, Guid>();
        if (requested is not null && requested.Count > 0)
        {
            var byId = allMessages.ToDictionary(message => message.MessageId);
            foreach (var (groupId, messageId) in requested)
            {
                if (!byId.TryGetValue(messageId, out var message) || message.VariantGroupId != groupId)
                {
                    // The message is not in this conversation, or it is not a member of the group it is keyed under.
                    throw new NodeChatInvalidBranchSelectionException(cutoff.ConversationId, groupId, messageId);
                }

                if (anchorSequence(message) > cutoffAnchor)
                {
                    // The group is anchored downstream of the branch point — not part of this branch. Drop it.
                    continue;
                }

                selection[groupId] = messageId;
            }
        }

        // The branch-point turn is authoritative for its own group: the user branched from THIS exact revision,
        // so it wins over any caller-supplied entry for the same group.
        if (cutoff.VariantGroupId is { } cutoffGroup)
        {
            selection[cutoffGroup] = cutoff.MessageId;
        }

        return selection.Count > 0 ? selection : null;
    }

    public async Task<NodeChatMessageVariantDto?> CreateMessageVariantAsync(NodeChatCreateMessageVariantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.NewMessageId == Guid.Empty || request.RequestId == Guid.Empty)
        {
            throw new ArgumentException("Variant messages require non-empty message and request ids.", nameof(request));
        }

        // Conversation-exclusive: minting a sibling variant allocates a new sequence, so it must serialize with every
        // other allocate/delete on the conversation exactly like the send placeholder insert.
        return await _writer.ExecuteConversationExclusiveAsync(request.ConversationId,
            async (dbContext, token) =>
            {
                // Read the original outside the write transaction (a raw read command cannot run under a Sqlite pending
                // transaction). The conversation-exclusive lock guarantees the row cannot change before the insert below.
                var original = await ReadMessageAsync(dbContext, request.ConversationId, request.OriginalMessageId, token);
                if (original is null)
                {
                    return null;
                }

                // The whole turn shares a variant group; mint one and back-stamp the original when it has none.
                var variantGroupId = original.VariantGroupId ?? Guid.NewGuid();
                var stampOriginal = original.VariantGroupId is null;
                var metadata = dbContext.EncryptMessageMetadata(SerializeMetadata(request.MetadataJson, reasoning: null, request.Model, inputTokens: null, outputTokens: null, totalTokens: null,
                        reasoningTokens: null, parts: null,
                        request.AgentDefinitionId, request.AgentName, request.ReasoningEffort),
                    request.ConversationId,
                    request.NewMessageId);

                var attempt = 0;
                while (true)
                {
                    attempt++;
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
                    var dbTransaction = transaction.GetDbTransaction();
                    var sequence = await NextSequenceAsync(dbContext, request.ConversationId, dbTransaction, token);
                    try
                    {
                        if (stampOriginal)
                        {
                            await using var stampCommand = dbContext.Database.GetDbConnection().CreateCommand();
                            stampCommand.Transaction = dbTransaction;
                            stampCommand.CommandText = "UPDATE messages SET variant_group_id = $variant_group_id WHERE conversation_id = $conversation_id AND message_id = $message_id;";
                            AddParameter(stampCommand, "$variant_group_id", variantGroupId);
                            AddParameter(stampCommand, "$conversation_id", request.ConversationId);
                            AddParameter(stampCommand, "$message_id", request.OriginalMessageId);
                            await OpenIfNeededAsync(stampCommand.Connection, token);
                            await stampCommand.ExecuteNonQueryAsync(token);
                        }

                        // The new sibling variant is an assistant placeholder with the same parent and a shared group,
                        // stamped with the per-response agent attribution so the pending variant shows the agent name.
                        await using var insertCommand = dbContext.Database.GetDbConnection().CreateCommand();
                        insertCommand.Transaction = dbTransaction;
                        insertCommand.CommandText = """
                                                    INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id, agent_definition_id)
                                                    VALUES ($message_id, $conversation_id, $sequence, $role, '', $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, NULL, $origin, $parent_message_id, $variant_group_id, $agent_definition_id);
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
                        // Plaintext per-message agent attribution (regenerate + branch siblings): mirrors the send-placeholder
                        // insert so per-variant feedback aggregates by the resolved agent without decrypting metadata.
                        AddParameter(insertCommand, "$agent_definition_id", request.AgentDefinitionId);
                        await OpenIfNeededAsync(insertCommand.Connection, token);
                        await insertCommand.ExecuteNonQueryAsync(token);

                        // Minting a sibling shifts the default selected path, so any compaction synopsis is cleared in
                        // the same transaction. simplified: blunt clear, a covered-span hash would invalidate less often.
                        await using var clearSummaryCommand = dbContext.Database.GetDbConnection().CreateCommand();
                        clearSummaryCommand.Transaction = dbTransaction;
                        clearSummaryCommand.CommandText =
                            "UPDATE conversations SET compaction_summary = NULL, compaction_summary_covers_to_sequence = NULL, compaction_summary_updated_at_utc = NULL WHERE conversation_id = $conversation_id;";
                        AddParameter(clearSummaryCommand, "$conversation_id", request.ConversationId);
                        await clearSummaryCommand.ExecuteNonQueryAsync(token);

                        await TouchConversationAsync(dbContext, request.ConversationId, request.CreatedAtUtc, token);
                        await transaction.CommitAsync(token);

                        var variant = new NodeChatPersistedMessageDto
                        {
                            MessageId = request.NewMessageId,
                            ConversationId = request.ConversationId,
                            RequestId = request.RequestId,
                            Sequence = sequence,
                            Role = AssistantRole,
                            Content = string.Empty,
                            Reasoning = null,
                            Status = NodeChatMessageStatusValues.Pending,
                            CreatedAtUtc = request.CreatedAtUtc,
                            UpdatedAtUtc = request.CreatedAtUtc,
                            Model = request.Model,
                            Error = null,
                            MetadataJson = request.MetadataJson,
                            Origin = NodeChatOriginValues.Local,
                            ParentMessageId = request.OriginalMessageId,
                            VariantGroupId = variantGroupId,
                            AgentDefinitionId = request.AgentDefinitionId,
                            AgentName = request.AgentName,
                            ReasoningEffort = request.ReasoningEffort
                        };

                        return new NodeChatMessageVariantDto { VariantGroupId = variantGroupId, OriginalMessageId = request.OriginalMessageId, Variant = variant };
                    }
                    catch (Exception exception) when (IsUniqueConstraintViolation(exception) && attempt < MaxSequenceAllocationAttempts)
                    {
                        await transaction.RollbackAsync(token);
                    }
                }
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ListMessageVariantsAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteConversationSharedAsync(conversationId,
            async (dbContext, token) =>
            {
                var messages = await ReadMessagesAsync(dbContext, conversationId, token);
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
            cancellationToken);
    }
}
