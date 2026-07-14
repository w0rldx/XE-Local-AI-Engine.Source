namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

    // Correlated message update, keyed on (conversation, message, request). The guarded variant additionally requires the
    // current status to be one of the three source statuses bound below, so a guarded transition (the cancel path) is
    // rejected atomically at the SQLite layer once the row has reached a terminal status.
    private const string CorrelatedUpdateSql = """
                                               UPDATE messages
                                               SET content = $content, metadata_json = $metadata_json, updated_at_utc = $updated_at_utc, status = $status, error = $error
                                               WHERE conversation_id = $conversation_id
                                                 AND message_id = $message_id
                                                 AND request_id = $request_id;
                                               """;

    private const string CorrelatedUpdateWithSourceStatusGuardSql = """
                                                                    UPDATE messages
                                                                    SET content = $content, metadata_json = $metadata_json, updated_at_utc = $updated_at_utc, status = $status, error = $error
                                                                    WHERE conversation_id = $conversation_id
                                                                      AND message_id = $message_id
                                                                      AND request_id = $request_id
                                                                      AND status IN ($required_status_0, $required_status_1, $required_status_2);
                                                                    """;

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
            requestId: null,
            UserRole,
            request.Content.Trim(),
            reasoning: null,
            NodeChatMessageStatusValues.Completed,
            request.CreatedAtUtc,
            request.CreatedAtUtc,
            model: null,
            error: null,
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
            reasoning: null,
            NodeChatMessageStatusValues.Pending,
            request.CreatedAtUtc,
            request.CreatedAtUtc,
            request.Model,
            error: null,
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
            content: null,
            reasoning: null,
            error: null,
            model: null,
            inputTokens: null,
            outputTokens: null,
            totalTokens: null,
            reasoningTokens: null,
            replaceContent: true,
            cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> MarkAssistantStreamingAsync(NodeChatMessageCorrelation correlation, long updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return UpdateCorrelatedMessageAsync(correlation,
            updatedAtUtc,
            NodeChatMessageStatusValues.Streaming,
            content: null,
            reasoning: null,
            error: null,
            model: null,
            inputTokens: null,
            outputTokens: null,
            totalTokens: null,
            reasoningTokens: null,
            replaceContent: true,
            cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> FlushAssistantPartialAsync(NodeChatPartialFlushRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return UpdateCorrelatedMessageAsync(request.Correlation,
            request.UpdatedAtUtc,
            status: null,
            request.Content,
            request.Reasoning,
            error: null,
            model: null,
            inputTokens: null,
            outputTokens: null,
            totalTokens: null,
            reasoningTokens: null,
            request.ReplaceContent,
            cancellationToken,
            // A partial flush is a mid-stream content advance, not a conversation-level event: it fires per debounce
            // window and would otherwise run a second UPDATE (conversation touch) every time. The conversation was
            // already touched when the turn started (placeholder/queued/streaming) and is touched again at terminalize,
            // so skipping it here drops a redundant write from the hot streaming path without changing recency order.
            touchConversation: false);
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
            replaceContent: true,
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
            content: null,
            reasoning: null,
            error: null,
            model: null,
            inputTokens: null,
            outputTokens: null,
            totalTokens: null,
            reasoningTokens: null,
            replaceContent: true,
            cancellationToken,
            requiredCurrentStatuses: NodeChatMessageStatusValues.Cancellable).ConfigureAwait(false);

        // The guard leaves an already-terminal message untouched, so report the true persisted status and only claim a
        // cancellation when the message actually landed in the Cancelled state. This is idempotent: a repeat cancel of an
        // already-cancelled message reports Cancelled with no second rewrite, while a cancel that raced a completed /
        // failed / interrupted terminalize reports that terminal status with Cancelled = false.
        var cancelled = string.Equals(message.Status, NodeChatMessageStatusValues.Cancelled, StringComparison.Ordinal);
        return new NodeChatCancelResultDto(request.Correlation, message.Status, cancelled);
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
        var metadata = SerializeMetadata(metadataJson, reasoning, model, inputTokens: null, outputTokens: null, totalTokens: null, reasoningTokens: null, parts: null, agentDefinitionId,
            agentName, reasoningEffort);

        // Conversation-exclusive: sequence allocation + insert must not interleave with another insert or a delete on
        // the same conversation. The allocate + insert + conversation-touch run in ONE transaction so a failed insert
        // rolls the allocation back cleanly and the retry re-reads a fresh MAX(sequence).
        return await _writer.ExecuteConversationExclusiveAsync(conversationId,
            async (dbContext, token) =>
            {
                var attempt = 0;
                while (true)
                {
                    attempt++;
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false);
                    var dbTransaction = transaction.GetDbTransaction();
                    var sequence = await NextSequenceAsync(dbContext, conversationId, dbTransaction, token).ConfigureAwait(false);
                    try
                    {
                        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                        command.Transaction = dbTransaction;
                        command.CommandText = """
                                              INSERT INTO messages (message_id, conversation_id, sequence, role, content, metadata_json, created_at_utc, updated_at_utc, status, request_id, error, origin, parent_message_id, variant_group_id, agent_definition_id)
                                              VALUES ($message_id, $conversation_id, $sequence, $role, $content, $metadata_json, $created_at_utc, $updated_at_utc, $status, $request_id, $error, $origin, $parent_message_id, $variant_group_id, $agent_definition_id);
                                              """;
                        AddParameter(command, "$message_id", messageId);
                        AddParameter(command, "$conversation_id", conversationId);
                        AddParameter(command, "$sequence", sequence);
                        AddParameter(command, "$role", role);
                        AddParameter(command, "$content", dbContext.EncryptMessageContent(content, conversationId, messageId));
                        AddParameter(command, "$metadata_json", dbContext.EncryptMessageMetadata(metadata, conversationId, messageId));
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
                        await transaction.CommitAsync(token).ConfigureAwait(false);

                        return new NodeChatPersistedMessageDto(messageId, conversationId, requestId, sequence, role, content, reasoning, status, createdAtUtc, updatedAtUtc, model, error,
                            metadataJson, Origin: origin, ParentMessageId: parentMessageId, VariantGroupId: variantGroupId, AgentDefinitionId: agentDefinitionId, AgentName: agentName,
                            ReasoningEffort: reasoningEffort);
                    }
                    catch (Exception exception) when (IsUniqueConstraintViolation(exception) && attempt < MaxSequenceAllocationAttempts)
                    {
                        await transaction.RollbackAsync(token).ConfigureAwait(false);
                    }
                }
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
        long? generationDurationMs = null,
        bool touchConversation = true,
        IReadOnlySet<string>? requiredCurrentStatuses = null)
    {
        ValidateCorrelation(correlation);

        // Message-payload update to an already-allocated row: parallel with updates to OTHER messages, serialized per
        // message, and excluded against a conversation delete via the shared/exclusive hierarchy.
        return await _writer.ExecuteMessageUpdateAsync(correlation.ConversationId,
            correlation.MessageId,
            async (dbContext, token) =>
            {
                var current = await ReadMessageAsync(dbContext, correlation.ConversationId, correlation.MessageId, token).ConfigureAwait(false)
                              ?? throw new InvalidOperationException("The correlated node chat message was not found.");
                if (current.RequestId != correlation.RequestId)
                {
                    throw new InvalidOperationException("The correlated node chat request id did not match the persisted message.");
                }

                // Transition guard (currently the cancel path): a status change is only allowed from one of the source
                // statuses supplied by the caller. Once the row has left that set — e.g. a completed / failed / interrupted
                // terminalize already ran — the update is skipped and the true current state is returned unchanged, so a
                // late cancel can never rewrite a finished message. The per-message write lock makes this read
                // authoritative; the AND status IN (...) predicate below re-enforces it atomically at the SQLite layer.
                if (requiredCurrentStatuses is not null && !requiredCurrentStatuses.Contains(current.Status))
                {
                    return current;
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
                // Two constant statements (never string-built from input): the guarded form appends the atomic
                // 'AND status IN (...)' source-status predicate so the transition is rejected at the SQLite layer if the
                // row is no longer in the permitted set. Its placeholder count matches the cancellable status set below.
                if (requiredCurrentStatuses is null)
                {
                    command.CommandText = CorrelatedUpdateSql;
                }
                else
                {
                    command.CommandText = CorrelatedUpdateWithSourceStatusGuardSql;
                }

                AddParameter(command, "$content", dbContext.EncryptMessageContent(nextContent, correlation.ConversationId, correlation.MessageId));
                AddParameter(command, "$metadata_json", dbContext.EncryptMessageMetadata(metadata, correlation.ConversationId, correlation.MessageId));
                AddParameter(command, "$updated_at_utc", updatedAtUtc);
                AddParameter(command, "$status", nextStatus);
                AddParameter(command, "$error", nextError);
                AddParameter(command, "$conversation_id", correlation.ConversationId);
                AddParameter(command, "$message_id", correlation.MessageId);
                AddParameter(command, "$request_id", correlation.RequestId);
                if (requiredCurrentStatuses is not null)
                {
                    var statusIndex = 0;
                    foreach (var requiredStatus in requiredCurrentStatuses)
                    {
                        AddParameter(command, $"$required_status_{statusIndex}", requiredStatus);
                        statusIndex++;
                    }
                }

                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                var affected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (requiredCurrentStatuses is not null && affected == 0)
                {
                    // The atomic predicate rejected the write because the row reached a terminal status; return the true
                    // current state without a rewrite or a conversation touch.
                    return current;
                }

                if (touchConversation)
                {
                    await TouchConversationAsync(dbContext, correlation.ConversationId, updatedAtUtc, token).ConfigureAwait(false);
                }

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
