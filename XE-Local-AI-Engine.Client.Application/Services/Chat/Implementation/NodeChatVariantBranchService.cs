namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Variant + branch commands behind <see cref="NodeChatPersistenceService" />: recording a regenerated turn as a
///     sibling variant, listing a turn's variants, and branching a conversation into a new local thread. Reads the
///     branch source via <see cref="NodeChatReadModel" /> on its own write key before writing under the new
///     conversation's key (two serialized scopes avoid a cross-conversation lock-ordering hazard).
/// </summary>
internal sealed class NodeChatVariantBranchService(NodeChatPersistenceWriter writer, NodeChatReadModel readModel)
{
    private const string AssistantRole = "assistant";

    private readonly NodeChatReadModel _readModel = readModel ?? throw new ArgumentNullException(nameof(readModel));
    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public async Task<NodeChatBranchResultDto?> BranchConversationAsync(NodeChatBranchConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read the source (including its messages) on its own write key, then create the branch under the new
        // conversation's write key. Two serialized scopes avoid a cross-conversation lock-ordering hazard.
        var source = await _readModel.GetConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        var cutoff = source.Messages.FirstOrDefault(message => message.MessageId == request.MessageId);
        if (cutoff is null)
        {
            return null;
        }

        // Collapse variant groups so the branch is a LINEAR thread: exactly one revision per group, positioned at the
        // group's ANCHOR (its earliest member's sequence), mirroring the frontend, which renders a variant group at its
        // oldest sibling and lets any sibling be the active revision (MessageRevisionGrouping.ts). Anchoring by group —
        // rather than by each message's own sequence — is what makes late regenerations branch correctly: regenerating
        // an EARLY turn AFTER later turns exist mints a sibling whose sequence lands PAST those later turns, yet that
        // sibling still belongs to the early turn and must branch at the early position. The selected revision per group
        // comes from the caller-supplied map (the path the user was viewing); a group with no valid selection falls back
        // to its newest member. The branch-point turn always contributes exactly the cutoff message, overriding any
        // caller entry for its group. Without this collapse the copied siblings would render as duplicate stacked
        // assistant turns (variant_group_id is dropped on copy below). (RC fix + late-sibling anchoring.)
        var anchorByGroup = source.Messages.Where(message => message.VariantGroupId is not null)
                                  .GroupBy(message => message.VariantGroupId!.Value)
                                  .ToDictionary(group => group.Key, group => group.Min(member => member.Sequence));

        int AnchorSequence(NodeChatPersistedMessageDto message) =>
            message.VariantGroupId is { } groupId ? anchorByGroup[groupId] : message.Sequence;

        // The cutoff's anchored position defines how far the branch reaches: a group participates iff its own anchor is
        // at/upstream of it. When the cutoff is itself a late-created sibling of an early turn, this resolves to the
        // early position it renders at, not its late raw sequence.
        var cutoffAnchor = AnchorSequence(cutoff);
        var eligible = source.Messages.Where(message => AnchorSequence(message) <= cutoffAnchor).ToArray();
        var selection = BuildValidatedSelection(request.SelectedRevisions, source.Messages, cutoff, anchorByGroup, cutoffAnchor);
        // The resolver orders by each chosen sibling's own sequence; re-order by anchor so a late-created sibling lands
        // at its group's position instead of the tail. Each copy is also stamped with the anchor sequence below.
        IReadOnlyList<NodeChatPersistedMessageDto> copies =
            SelectedPathResolver.Resolve(eligible, selection).OrderBy(AnchorSequence).ToArray();
        var branchedConversationId = Guid.NewGuid();

        return await _writer.ExecuteConversationExclusiveAsync(branchedConversationId,
            async (dbContext, token) =>
            {
                // One transaction around the whole branch (conversation insert + every message copy): the copies are
                // separate INSERT statements, so without this a cancellation/failure mid-loop would autocommit the
                // conversation row plus a prefix of its messages, leaving a visible half-copied branch. Mirrors
                // CreateMessageVariantAsync's transactional insert.
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false);
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
                await OpenIfNeededAsync(conversationCommand.Connection, token).ConfigureAwait(false);
                await conversationCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                foreach (var message in copies)
                {
                    await using var messageCommand = dbContext.Database.GetDbConnection().CreateCommand();
                    messageCommand.Transaction = dbTransaction;
                    messageCommand.CommandText = """
                                                 INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id)
                                                 VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, $error, $origin, $parent_message_id, $variant_group_id);
                                                 """;
                    // A branch copy is a new row with a fresh message id, so its content/metadata envelope AAD binds the
                    // new (branchedConversationId, copyMessageId) pair. message.Content arrives already decrypted from
                    // the read model, so it is re-encrypted here under the copy's identity.
                    var copyMessageId = Guid.NewGuid();
                    AddParameter(messageCommand, "$message_id", copyMessageId);
                    AddParameter(messageCommand, "$conversation_id", branchedConversationId);
                    // Stamp the copy at its group's anchored position (not the chosen sibling's own sequence) so a
                    // late-created sibling of an early turn lands where the turn renders, keeping the new linear thread
                    // ordered exactly as the operator saw it. Anchor sequences are unique (each is a distinct source row).
                    AddParameter(messageCommand, "$sequence", AnchorSequence(message));
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
                    await OpenIfNeededAsync(messageCommand.Connection, token).ConfigureAwait(false);
                    await messageCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return new NodeChatBranchResultDto(request.ConversationId, branchedConversationId, copies.Count);
            },
            cancellationToken).ConfigureAwait(false);
    }

    // Validates the caller-supplied selected-revision map against the source conversation and reduces it to the
    // entries that actually bear on the branch (upstream of the cutoff). Fails CLOSED on an integrity violation —
    // an entry whose message does not belong to the conversation, or that is keyed under a variant group it is not
    // a member of — because such an entry can only come from a stale/tampered client and must not silently pick a
    // fallback revision. An otherwise-valid entry for a group whose ANCHOR sits after the cutoff (a group the user
    // navigated downstream of the branch point) is simply dropped: it plays no part in this branch's linear thread.
    // The comparison is on the group anchor, NOT the selected message's own sequence — a legitimately selected
    // late-created sibling of an early (upstream) turn carries a sequence past the cutoff yet still belongs to the
    // branch, and must be kept. Returns null when there is nothing to pin, driving the legacy newest-per-group default.
    private static IReadOnlyDictionary<Guid, Guid>? BuildValidatedSelection(IReadOnlyDictionary<Guid, Guid>? requested,
        IReadOnlyList<NodeChatPersistedMessageDto> allMessages,
        NodeChatPersistedMessageDto cutoff,
        IReadOnlyDictionary<Guid, int> anchorByGroup,
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

                if (anchorByGroup.GetValueOrDefault(groupId, message.Sequence) > cutoffAnchor)
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
                var original = await ReadMessageAsync(dbContext, request.ConversationId, request.OriginalMessageId, token).ConfigureAwait(false);
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
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false);
                    var dbTransaction = transaction.GetDbTransaction();
                    var sequence = await NextSequenceAsync(dbContext, request.ConversationId, dbTransaction, token).ConfigureAwait(false);
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
                            await OpenIfNeededAsync(stampCommand.Connection, token).ConfigureAwait(false);
                            await stampCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }

                        // The new sibling variant is an assistant placeholder: same parent (the user turn), shared group.
                        // The per-response agent attribution is stamped at mint time so the pending variant already
                        // carries the agent name (symmetric with the send placeholder).
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
                        await OpenIfNeededAsync(insertCommand.Connection, token).ConfigureAwait(false);
                        await insertCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                        // Minting a new sibling shifts the default selected path (newest sibling wins), which invalidates
                        // any compaction synopsis built from the prior selection. Clear it in the same transaction so a
                        // later send never injects a stale summary. ponytail: blunt clear — also drops the synopsis when
                        // the regenerated turn is newer than the covered range (harmless, the user re-compacts); a
                        // covered-span hash would invalidate only when the covered messages actually change.
                        await using var clearSummaryCommand = dbContext.Database.GetDbConnection().CreateCommand();
                        clearSummaryCommand.Transaction = dbTransaction;
                        clearSummaryCommand.CommandText =
                            "UPDATE conversations SET compaction_summary = NULL, compaction_summary_covers_to_sequence = NULL, compaction_summary_updated_at_utc = NULL WHERE conversation_id = $conversation_id;";
                        AddParameter(clearSummaryCommand, "$conversation_id", request.ConversationId);
                        await clearSummaryCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                        await TouchConversationAsync(dbContext, request.ConversationId, request.CreatedAtUtc, token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);

                        var variant = new NodeChatPersistedMessageDto(request.NewMessageId,
                            request.ConversationId,
                            request.RequestId,
                            sequence,
                            AssistantRole,
                            string.Empty,
                            Reasoning: null,
                            NodeChatMessageStatusValues.Pending,
                            request.CreatedAtUtc,
                            request.CreatedAtUtc,
                            request.Model,
                            Error: null,
                            request.MetadataJson,
                            Origin: NodeChatOriginValues.Local,
                            ParentMessageId: request.OriginalMessageId,
                            VariantGroupId: variantGroupId,
                            AgentDefinitionId: request.AgentDefinitionId,
                            AgentName: request.AgentName,
                            ReasoningEffort: request.ReasoningEffort);

                        return new NodeChatMessageVariantDto(variantGroupId, request.OriginalMessageId, variant);
                    }
                    catch (Exception exception) when (IsUniqueConstraintViolation(exception) && attempt < MaxSequenceAllocationAttempts)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NodeChatPersistedMessageDto>> ListMessageVariantsAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return await _writer.ExecuteConversationSharedAsync(conversationId,
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
}
