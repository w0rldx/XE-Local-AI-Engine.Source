namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using static NodeChatMetadataSerializer;
using static NodeChatPersistenceSql;

/// <summary>
///     Message-write commands behind <see cref="NodeChatPersistenceService" />: user-message persistence, the
///     assistant placeholder, and the correlated status and content transitions.
/// </summary>
/// <remarks>
///     It shares the single <see cref="NodeChatPersistenceWriter" /> so per-message write-key serialization holds.
/// </remarks>
internal sealed class NodeChatMessageCommands
{
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    // Upper bound on the distinct source statuses a guarded transition can enumerate, the largest set in
    // NodeChatMessageTransitions. A smaller set repeats a real member, so the IN clause stays a fixed constant.
    private const int MaxSourceStatusSlots = 4;

    public NodeChatMessageCommands(NodeChatPersistenceWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
    }

    // How a run-envelope write reconciles with one the message may already have: InsertIfAbsent keeps the first write,
    // Upsert lets the pump's terminalize enrich a thin cancel envelope so its status and tokens match the row.
    private enum RunEnvelopeWriteMode
    {
        InsertIfAbsent,
        Upsert
    }

    // Correlated message update, keyed on (conversation, message, request). The guarded variant also requires the
    // current status to be a bound source status, so a transition is rejected atomically at the SQLite layer.
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
                                                                      AND status IN ($required_status_0, $required_status_1, $required_status_2, $required_status_3);
                                                                    """;

    // The run-envelope column list, shared by both write statements below so they stay in lockstep.
    private const string EnvelopeColumns = """
                                           (id, record_kind, schema_version, agent_definition_id, conversation_id, message_id, invocation_id, request_id,
                                            model_name, provider, config_hash, terminal_status, latency_ms, prompt_tokens, completion_tokens, reasoning_tokens, total_tokens,
                                            content_chunk_count, reasoning_chunk_count, trace_id, started_at_utc, tool_schema_tokens, max_tool_schema_tokens,
                                            dispatched_tier, authored_effort, model_readiness_ms, success, error_class, created_at_utc)
                                           """;

    private const string EnvelopeValues = """
                                          $id, $record_kind, $schema_version, $agent_definition_id, $conversation_id, $message_id, $invocation_id, $request_id,
                                          $model_name, $provider, $config_hash, $terminal_status, $latency_ms, $prompt_tokens, $completion_tokens, $reasoning_tokens, $total_tokens,
                                          $content_chunk_count, $reasoning_chunk_count, $trace_id, $started_at_utc, $tool_schema_tokens, $max_tool_schema_tokens,
                                          $dispatched_tier, $authored_effort, $model_readiness_ms, $success, $error_class, $created_at_utc
                                          """;

    // InsertIfAbsent: the WHERE NOT EXISTS on (record_kind, message_id) makes the write a no-op when the message already
    // has an envelope, so a startup-reconcile backfill or a cancel that lost the race never duplicates/clobbers one.
    private const string EnvelopeInsertIfAbsentSql = $"""
                                                      INSERT INTO agent_execution_logs
                                                          {EnvelopeColumns}
                                                      SELECT {EnvelopeValues}
                                                      WHERE NOT EXISTS (SELECT 1 FROM agent_execution_logs WHERE record_kind = $record_kind AND message_id = $message_id);
                                                      """;

    // The ChatRunEnvelope record_kind as a SQL literal, which must equal the enum value or the upsert's ON CONFLICT
    // WHERE resolves against no index and SQLite throws. A const string keeps EnvelopeUpsertSql constant (CA2100).
    private const string EnvelopeRecordKindLiteral = "1";

    // Upsert: the pump's terminalize wins, overwriting every run-outcome column of a thin prior envelope in place. The
    // conflict target's WHERE mirrors the filtered unique index, and the conflict keys stay as first written.
    private const string EnvelopeUpsertSql = $"""
                                              INSERT INTO agent_execution_logs
                                                  {EnvelopeColumns}
                                              VALUES ({EnvelopeValues})
                                              ON CONFLICT (message_id) WHERE record_kind = {EnvelopeRecordKindLiteral}
                                              DO UPDATE SET
                                                  schema_version = excluded.schema_version,
                                                  agent_definition_id = excluded.agent_definition_id,
                                                  invocation_id = excluded.invocation_id,
                                                  request_id = excluded.request_id,
                                                  model_name = excluded.model_name,
                                                  provider = excluded.provider,
                                                  config_hash = excluded.config_hash,
                                                  terminal_status = excluded.terminal_status,
                                                  latency_ms = excluded.latency_ms,
                                                  prompt_tokens = excluded.prompt_tokens,
                                                  completion_tokens = excluded.completion_tokens,
                                                  reasoning_tokens = excluded.reasoning_tokens,
                                                  total_tokens = excluded.total_tokens,
                                                  content_chunk_count = excluded.content_chunk_count,
                                                  reasoning_chunk_count = excluded.reasoning_chunk_count,
                                                  trace_id = excluded.trace_id,
                                                  started_at_utc = excluded.started_at_utc,
                                                  tool_schema_tokens = excluded.tool_schema_tokens,
                                                  max_tool_schema_tokens = excluded.max_tool_schema_tokens,
                                                  dispatched_tier = excluded.dispatched_tier,
                                                  authored_effort = excluded.authored_effort,
                                                  model_readiness_ms = excluded.model_readiness_ms,
                                                  success = excluded.success,
                                                  error_class = excluded.error_class,
                                                  created_at_utc = excluded.created_at_utc;
                                              """;

    private readonly NodeChatPersistenceWriter _writer;

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

    /// <summary>Inserts a Completed user or assistant message under the caller's deterministic id, or answers the row already standing under it.</summary>
    /// <remarks>
    ///     Idempotency is that id plus an existence check inside the conversation-exclusive section: the <c>request_id</c>
    ///     index is non-unique, and a bare insert would read a duplicate id as a sequence collision.
    /// </remarks>
    public Task<NodeChatInsertMessageIfAbsentResult> InsertMessageIfAbsentAsync(NodeChatInsertMessageIfAbsentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Message content must be provided.", nameof(request));
        }

        var isAssistant = string.Equals(request.Role, AssistantRole, StringComparison.Ordinal);
        if (!isAssistant && !string.Equals(request.Role, UserRole, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Role '{request.Role}' is neither '{UserRole}' nor '{AssistantRole}'.", nameof(request));
        }

        return InsertMessageCoreAsync(request.ConversationId,
            request.MessageId,
            requestId: null,
            request.Role,
            isAssistant ? request.Content : request.Content.Trim(),
            reasoning: null,
            NodeChatMessageStatusValues.Completed,
            request.CreatedAtUtc,
            request.CreatedAtUtc,
            isAssistant ? request.Model : null,
            error: null,
            metadataJson: null,
            NodeChatOriginValues.Local,
            cancellationToken,
            agentDefinitionId: isAssistant ? request.AgentDefinitionId : null,
            agentName: isAssistant ? request.AgentName : null,
            ifAbsent: true,
            // A finished assistant row carries its run envelope from the start, in the insert's transaction, so the restart
            // reconcile never backfills it as a chat run. Thin, like the cancel path's, and never clobbering an existing one.
            envelope: isAssistant ? new AgentRunEnvelopeMetadata { InvocationId = null, DurationMs = 0L, TraceId = CurrentTraceId() } : null);
    }

    /// <summary>Removes one message the caller itself inserted, with any run envelope it carries — a compensating write, never a chat delete.</summary>
    public Task DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default) =>
        _writer.ExecuteConversationExclusiveAsync(conversationId,
            async (dbContext, token) =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = """
                                      DELETE FROM agent_execution_logs WHERE message_id = $message_id AND conversation_id = $conversation_id;
                                      DELETE FROM messages WHERE message_id = $message_id AND conversation_id = $conversation_id;
                                      """;
                AddParameter(command, "$message_id", messageId);
                AddParameter(command, "$conversation_id", conversationId);
                await OpenIfNeededAsync(command.Connection, token);
                _ = await command.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
                return true;
            },
            cancellationToken);

    /// <summary>The conversation a message id belongs to, or null when no message carries it.</summary>
    public Task<Guid?> GetMessageConversationIdAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        _writer.ExecuteConversationSharedAsync(Guid.Empty,
            async (dbContext, token) =>
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT conversation_id FROM messages WHERE message_id = $message_id;";
                AddParameter(command, "$message_id", messageId);
                await OpenIfNeededAsync(command.Connection, token);
                return await command.ExecuteScalarAsync(token) is string owner ? Guid.Parse(owner) : (Guid?)null;
            },
            cancellationToken);

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
            cancellationToken,
            // Only from Pending: a cancel that raced ahead of run ownership (before the cancellation registration exists)
            // must not be overwritten back to Queued. A rejected mark returns the true (terminal) row so the caller aborts.
            requiredCurrentStatuses: NodeChatMessageTransitions.QueuedSources);
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
            cancellationToken,
            // Only from Pending (platform path) or Queued (local path): a stream can never resurrect a terminal row. A
            // rejected mark returns the true (terminal) row so the caller aborts instead of streaming into it.
            requiredCurrentStatuses: NodeChatMessageTransitions.StreamingSources);
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
            // A partial flush is a mid-stream content advance, not a conversation-level event, and the conversation was
            // already touched at the turn's start and is touched again at terminalize, so recency order is unchanged.
            touchConversation: false,
            // A late flush must never mutate a row that already terminalized (or was cancelled): guard to the non-terminal
            // source set so a debounced tail arriving after the terminal is an atomic no-op.
            requiredCurrentStatuses: NodeChatMessageTransitions.FlushSources);
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
            request.GenerationDurationMs,
            requiredCurrentStatuses: NodeChatMessageTransitions.TerminalizeSources(request.Status),
            // Durable run envelope written atomically with the terminal row: both commit or roll back together. Upsert,
            // so the winning terminalize overwrites any thin envelope a prior cancel wrote for this message.
            envelope: request.Envelope,
            envelopeWriteMode: RunEnvelopeWriteMode.Upsert,
            // KB sources that grounded this turn; null on paths that retrieved nothing preserves any
            // existing persisted sources, just like Parts.
            sources: request.Sources);
    }

    public async Task<NodeChatCancelResultDto> CancelMessageAsync(NodeChatCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A cancel is a terminal transition, so it writes its envelope in the same guarded UPDATE, or a cancel before
        // queued leaves the row envelope-less until the next reconcile. Thin, and InsertIfAbsent so it never clobbers.
        var envelope = new AgentRunEnvelopeMetadata { InvocationId = null, DurationMs = 0L, TraceId = CurrentTraceId() };

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
            requiredCurrentStatuses: NodeChatMessageTransitions.CancelSources,
            envelope: envelope,
            envelopeWriteMode: RunEnvelopeWriteMode.InsertIfAbsent);

        // The guard leaves an already-terminal message untouched, so report the true persisted status and claim a
        // cancellation only when the row actually landed in Cancelled. A repeat cancel is therefore idempotent.
        var cancelled = string.Equals(message.Status, NodeChatMessageStatusValues.Cancelled, StringComparison.Ordinal);
        return new NodeChatCancelResultDto { Correlation = request.Correlation, Status = message.Status, Cancelled = cancelled };
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
        string? reasoningEffort = null) =>
        (await InsertMessageCoreAsync(conversationId, messageId, requestId, role, content, reasoning, status, createdAtUtc, updatedAtUtc, model, error, metadataJson, origin,
            cancellationToken, parentMessageId, variantGroupId, agentDefinitionId, agentName, reasoningEffort)).Message;

    private async Task<NodeChatInsertMessageIfAbsentResult> InsertMessageCoreAsync(Guid conversationId,
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
        string? reasoningEffort = null,
        bool ifAbsent = false,
        AgentRunEnvelopeMetadata? envelope = null)
    {
        var metadata = SerializeMetadata(metadataJson, reasoning, model, inputTokens: null, outputTokens: null, totalTokens: null, reasoningTokens: null, parts: null, agentDefinitionId,
            agentName, reasoningEffort);

        // Conversation-exclusive: the sequence allocation and insert must not interleave with another insert or delete
        // on this conversation, and they share ONE transaction so a failed insert rolls the allocation back.
        return await _writer.ExecuteConversationExclusiveAsync(conversationId,
            async (dbContext, token) =>
            {
                // Inside the exclusive section, so no concurrent insert of the same id can land between this read and the write below.
                if (ifAbsent && await ReadExistingAsync(dbContext, conversationId, messageId, token) is { } existing)
                {
                    return new NodeChatInsertMessageIfAbsentResult { Message = existing, Inserted = false };
                }

                var attempt = 0;
                while (true)
                {
                    attempt++;
                    await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
                    var dbTransaction = transaction.GetDbTransaction();
                    var sequence = await NextSequenceAsync(dbContext, conversationId, dbTransaction, token);
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
                        await OpenIfNeededAsync(command.Connection, token);
                        await command.ExecuteNonQueryAsync(token);

                        if (envelope is not null)
                        {
                            await WriteRunEnvelopeRowAsync(dbContext, dbTransaction, conversationId, messageId, requestId, agentDefinitionId, status, model,
                                promptTokens: null, completionTokens: null, reasoningTokens: null, totalTokens: null, envelope, createdAtUtc, RunEnvelopeWriteMode.InsertIfAbsent, token);
                        }

                        await TouchConversationAsync(dbContext, conversationId, updatedAtUtc, token);
                        await transaction.CommitAsync(token);

                        var message = new NodeChatPersistedMessageDto
                        {
                            MessageId = messageId,
                            ConversationId = conversationId,
                            RequestId = requestId,
                            Sequence = sequence,
                            Role = role,
                            Content = content,
                            Reasoning = reasoning,
                            Status = status,
                            CreatedAtUtc = createdAtUtc,
                            UpdatedAtUtc = updatedAtUtc,
                            Model = model,
                            Error = error,
                            MetadataJson = metadataJson,
                            Origin = origin,
                            ParentMessageId = parentMessageId,
                            VariantGroupId = variantGroupId,
                            AgentDefinitionId = agentDefinitionId,
                            AgentName = agentName,
                            ReasoningEffort = reasoningEffort
                        };
                        return new NodeChatInsertMessageIfAbsentResult { Message = message, Inserted = true };
                    }
                    catch (Exception exception) when (IsUniqueConstraintViolation(exception) && attempt < MaxSequenceAllocationAttempts)
                    {
                        await transaction.RollbackAsync(token);
                    }
                }
            },
            cancellationToken);
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
        IReadOnlySet<string>? requiredCurrentStatuses = null,
        AgentRunEnvelopeMetadata? envelope = null,
        RunEnvelopeWriteMode envelopeWriteMode = RunEnvelopeWriteMode.InsertIfAbsent,
        IReadOnlyList<NodeChatMessageSource>? sources = null)
    {
        ValidateCorrelation(correlation);
        if (requiredCurrentStatuses is { Count: 0 or > MaxSourceStatusSlots })
        {
            throw new ArgumentException($"A transition guard must name between 1 and {MaxSourceStatusSlots} source statuses.", nameof(requiredCurrentStatuses));
        }

        // Message-payload update to an already-allocated row: parallel with updates to OTHER messages, serialized per
        // message, and excluded against a conversation delete via the shared/exclusive hierarchy.
        return await _writer.ExecuteMessageUpdateAsync(correlation.ConversationId,
            correlation.MessageId,
            async (dbContext, token) =>
            {
                var current = await ReadMessageAsync(dbContext, correlation.ConversationId, correlation.MessageId, token)
                              ?? throw new NodeChatMessageCorrelationNotFoundException("The correlated node chat message was not found.");
                if (current.RequestId != correlation.RequestId)
                {
                    throw new NodeChatMessageCorrelationNotFoundException("The correlated node chat request id did not match the persisted message.");
                }

                // Transition guard: a write is allowed only from a source status the caller declared. The per-message
                // lock makes this read authoritative and the AND status IN (...) predicate below re-enforces it.
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
                // KB sources are reported once at terminalize; a null arg (partial flush) preserves any
                // existing value, mirroring the parts/duration preservation above.
                var nextSources = sources ?? current.Sources;
                // Agent attribution and the reasoning effort are stamped once at mint and never updated here, so they
                // are always preserved from current, or a later flush would re-serialize the blob without them.
                var metadata = SerializeMetadata(current.MetadataJson, nextReasoning, nextModel, nextInputTokens, nextOutputTokens, nextTotalTokens, nextReasoningTokens, nextParts,
                    current.AgentDefinitionId, current.AgentName, current.ReasoningEffort, nextGenerationDurationMs, nextSources);

                // With an envelope to write, the message UPDATE, the envelope insert and the conversation touch share
                // ONE transaction; a non-terminal update keeps the single-statement autocommit path of the hot path.
                var writeEnvelope = envelope is not null && IsTerminalStatus(nextStatus);
                await using var transaction = writeEnvelope
                    ? await dbContext.Database.BeginTransactionAsync(token)
                    : null;

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                if (transaction is not null)
                {
                    command.Transaction = transaction.GetDbTransaction();
                }

                // Two constant statements, never string-built from input: the guarded form appends the atomic
                // 'AND status IN (...)' predicate, whose placeholder count matches the status set bound below.
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
                    // Bind every one of the fixed IN slots. Spare slots (a set smaller than MaxSourceStatusSlots) repeat a
                    // real member rather than binding NULL, so the IN predicate stays exact without NULL-comparison subtlety.
                    var sourceStatuses = new List<string>(requiredCurrentStatuses);
                    for (var slot = 0; slot < MaxSourceStatusSlots; slot++)
                    {
                        AddParameter(command, $"$required_status_{slot}", sourceStatuses[slot < sourceStatuses.Count ? slot : sourceStatuses.Count - 1]);
                    }
                }

                await OpenIfNeededAsync(command.Connection, token);
                var affected = await command.ExecuteNonQueryAsync(token);
                if (requiredCurrentStatuses is not null && affected == 0)
                {
                    // The atomic predicate rejected the write because the row is terminal, so return the true current
                    // state with no rewrite, envelope or touch; an opened transaction disposes without a commit.
                    return current;
                }

                if (writeEnvelope)
                {
                    // The terminal status, success flag and bound agent id come from THIS winning write, so the envelope
                    // can never disagree with the row; the write mode governs reconciliation with an existing envelope.
                    await WriteRunEnvelopeRowAsync(dbContext,
                        transaction?.GetDbTransaction(),
                        correlation.ConversationId,
                        correlation.MessageId,
                        correlation.RequestId,
                        current.AgentDefinitionId,
                        nextStatus,
                        nextModel,
                        nextInputTokens,
                        nextOutputTokens,
                        nextReasoningTokens,
                        nextTotalTokens,
                        envelope!,
                        updatedAtUtc,
                        envelopeWriteMode,
                        token);
                }

                if (touchConversation)
                {
                    await TouchConversationAsync(dbContext, correlation.ConversationId, updatedAtUtc, token);
                }

                if (transaction is not null)
                {
                    await transaction.CommitAsync(token);
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
            cancellationToken);
    }

    // Writes the content-free run-envelope row on the caller's raw connection, enlisted in the terminalize transaction.
    // Metadata ONLY: no prompt, completion or tool-argument content. See docs/wiki/05-chat.md, "The run envelope".
    private static async Task WriteRunEnvelopeRowAsync(NodeChatDbContext dbContext,
        DbTransaction? transaction,
        Guid conversationId,
        Guid messageId,
        Guid? requestId,
        Guid? agentDefinitionId,
        string terminalStatus,
        string? model,
        int? promptTokens,
        int? completionTokens,
        int? reasoningTokens,
        int? totalTokens,
        AgentRunEnvelopeMetadata envelope,
        long createdAtUtc,
        RunEnvelopeWriteMode writeMode,
        CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        // Two compile-time-constant statements (never string-built from input); assigned separately rather than via a
        // conditional so each remains a constant CommandText (CA2100).
        if (writeMode == RunEnvelopeWriteMode.Upsert)
        {
            command.CommandText = EnvelopeUpsertSql;
        }
        else
        {
            command.CommandText = EnvelopeInsertIfAbsentSql;
        }

        AddParameter(command, "$id", Guid.NewGuid());
        AddParameter(command, "$record_kind", (int)AgentExecutionLogRecordKind.ChatRunEnvelope);
        AddParameter(command, "$schema_version", AgentRunEnvelope.CurrentSchemaVersion);
        // Bound agent id when the row carries one; Guid.Empty otherwise so agentless envelope rows share one retention
        // bucket and never surface in the per-agent diagnostics view.
        AddParameter(command, "$agent_definition_id", agentDefinitionId ?? Guid.Empty);
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$message_id", messageId);
        AddParameter(command, "$invocation_id", envelope.InvocationId);
        AddParameter(command, "$request_id", requestId);
        AddParameter(command, "$model_name", model ?? string.Empty);
        AddParameter(command, "$provider", envelope.Provider);
        AddParameter(command, "$config_hash", string.Empty);
        AddParameter(command, "$terminal_status", terminalStatus);
        AddParameter(command, "$latency_ms", envelope.DurationMs);
        // The envelope is the COST ledger, so it takes the turn totals summed over the provider rounds when the pump
        // supplied them. The message's own tokens are the LAST round's — context occupancy — and are the fallback.
        AddParameter(command, "$prompt_tokens", envelope.TurnInputTokens ?? promptTokens);
        AddParameter(command, "$completion_tokens", envelope.TurnOutputTokens ?? completionTokens);
        AddParameter(command, "$reasoning_tokens", envelope.TurnReasoningTokens ?? reasoningTokens);
        AddParameter(command, "$total_tokens", envelope.TurnTotalTokens ?? totalTokens);
        AddParameter(command, "$content_chunk_count", envelope.ContentChunkCount);
        AddParameter(command, "$reasoning_chunk_count", envelope.ReasoningChunkCount);
        AddParameter(command, "$trace_id", envelope.TraceId);
        AddParameter(command, "$started_at_utc", envelope.StartedAtUtc);
        AddParameter(command, "$tool_schema_tokens", envelope.ToolSchemaTokens);
        AddParameter(command, "$max_tool_schema_tokens", envelope.MaxToolSchemaTokens);
        AddParameter(command, "$dispatched_tier", envelope.DispatchedTier);
        AddParameter(command, "$authored_effort", envelope.AuthoredEffort);
        AddParameter(command, "$model_readiness_ms", envelope.ModelReadinessMs);
        AddParameter(command, "$success", string.Equals(terminalStatus, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal) ? 1 : 0);
        AddParameter(command, "$error_class", envelope.FailureCategory);
        AddParameter(command, "$created_at_utc", createdAtUtc);

        await OpenIfNeededAsync(command.Connection, cancellationToken);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The row an if-absent insert finds already standing. The id is looked up across EVERY conversation first: message_id
    // is the table's key, so the same id in another conversation is a caller bug the insert must refuse, not retry.
    private static async Task<NodeChatPersistedMessageDto?> ReadExistingAsync(NodeChatDbContext dbContext, Guid conversationId, Guid messageId, CancellationToken cancellationToken)
    {
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT conversation_id FROM messages WHERE message_id = $message_id;";
        AddParameter(command, "$message_id", messageId);
        await OpenIfNeededAsync(command.Connection, cancellationToken);
        if (await command.ExecuteScalarAsync(cancellationToken) is not string owner)
        {
            return null;
        }

        return Guid.Parse(owner) == conversationId
            ? await ReadMessageAsync(dbContext, conversationId, messageId, cancellationToken)
            : throw new InvalidOperationException($"Message '{messageId}' already belongs to another conversation.");
    }

    // W3C trace id of the ambient activity, for cross-correlation with exported traces, or null when no activity is in
    // scope. A default all-zero id counts as absent.
    private static string? CurrentTraceId()
    {
        if (Activity.Current is not { } activity)
        {
            return null;
        }

        var traceId = activity.TraceId;
        return traceId == default ? null : traceId.ToString();
    }
}
