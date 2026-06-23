namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Message-write commands behind <see cref="NodeChatPersistenceService" />: user-message persistence, the
///     assistant placeholder, and the correlated status/content transitions (queued/streaming/flush/terminalize/
///     cancel). Shares the single <see cref="NodeChatPersistenceWriter" /> so per-message write-key serialization is
///     preserved.
/// </summary>
internal sealed class NodeChatMessageCommands(NodeChatPersistenceWriter writer)
{
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    private readonly NodeChatPersistenceWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    public Task<NodeChatPersistedMessageDto> PersistUserMessageAsync(NodeChatPersistUserMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Message content must be provided.", nameof(request));
        }

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
        Guid? variantGroupId = null,
        Guid? agentDefinitionId = null,
        string? agentName = null,
        string? reasoningEffort = null)
    {
        return await _writer.ExecuteAsync(NodeChatPersistenceWriteKey.ForMessage(conversationId, messageId),
            async (dbContext, token) =>
            {
                var sequence = await NextSequenceAsync(dbContext, conversationId, token).ConfigureAwait(false);
                var metadata = SerializeMetadata(metadataJson, reasoning, model, null, null, null, null, null, agentDefinitionId, agentName, reasoningEffort);

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                                      INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id, agent_definition_id)
                                      VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, $error, $origin, $parent_message_id, $variant_group_id, $agent_definition_id);
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
                // Plaintext per-message agent attribution: lets feedback aggregate by the resolved agent without
                // decrypting the metadata blob. Stamped once at insert; later flush/terminalize never touch it.
                AddParameter(command, "$agent_definition_id", agentDefinitionId);
                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await TouchConversationAsync(dbContext, conversationId, updatedAtUtc, token).ConfigureAwait(false);

                return new NodeChatPersistedMessageDto(messageId, conversationId, requestId, sequence, role, content, reasoning, status, createdAtUtc, updatedAtUtc, model, error, metadataJson,
                    Origin: origin, ParentMessageId: parentMessageId, VariantGroupId: variantGroupId, AgentDefinitionId: agentDefinitionId, AgentName: agentName, ReasoningEffort: reasoningEffort);
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
        CancellationToken cancellationToken,
        IReadOnlyList<NodeChatMessagePart>? parts = null,
        long? generationDurationMs = null)
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
                // A null parts arg leaves the persisted parts untouched (a partial flush carries no parts); a
                // non-null list (including empty) is the authoritative interleave from terminalize and overwrites.
                var nextParts = parts ?? current.Parts;
                // The generation duration is reported once at terminalize; a null arg (partial flush) preserves any
                // existing value, mirroring the token-count preservation above.
                var nextGenerationDurationMs = generationDurationMs ?? current.GenerationDurationMs;
                // Agent attribution and the reasoning effort are stamped once at placeholder/variant mint and never
                // updated here, so always preserve them from current — otherwise a later flush/terminalize would
                // re-serialize the blob without those fields and silently drop the per-response attribution.
                var metadata = SerializeMetadata(current.MetadataJson, nextReasoning, nextModel, nextInputTokens, nextOutputTokens, nextTotalTokens, nextReasoningTokens, nextParts,
                    current.AgentDefinitionId, current.AgentName, current.ReasoningEffort, nextGenerationDurationMs);

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
                    ReasoningCount = nextReasoningTokens,
                    Parts = nextParts,
                    GenerationDurationMs = nextGenerationDurationMs
                };
            },
            cancellationToken).ConfigureAwait(false);
    }
}
