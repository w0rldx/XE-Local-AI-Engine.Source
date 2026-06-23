namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
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
                    AddParameter(messageCommand, "$parent_message_id", value: null);
                    AddParameter(messageCommand, "$variant_group_id", value: null);
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
                var metadata = SerializeMetadata(request.MetadataJson, reasoning: null, request.Model, inputTokens: null, outputTokens: null, totalTokens: null, reasoningTokens: null, parts: null,
                    request.AgentDefinitionId, request.AgentName, request.ReasoningEffort);

                await using var insertCommand = dbContext.Database.GetDbConnection().CreateCommand();
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

                await TouchConversationAsync(dbContext, request.ConversationId, request.CreatedAtUtc, token).ConfigureAwait(false);

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
}
