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
///     assistant placeholder, and the correlated status/content transitions (queued/streaming/flush/terminalize/
///     cancel). Shares the single <see cref="NodeChatPersistenceWriter" /> so per-message write-key serialization is
///     preserved.
/// </summary>
internal sealed class NodeChatMessageCommands(NodeChatPersistenceWriter writer)
{
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    // Upper bound on distinct source statuses a guarded transition can enumerate — the largest allowed-source set in
    // NodeChatMessageTransitions (the terminalize set: pending / queued / streaming / cancelled). Smaller sets bind the
    // spare slots by repeating a real member, so the IN clause stays a fixed constant statement.
    private const int MaxSourceStatusSlots = 4;

    // How a run-envelope write reconciles with an envelope the message may already have. InsertIfAbsent keeps the first
    // write (a pre-run cancel's thin envelope, a startup reconcile backfill) — never clobbering a real one. Upsert lets
    // the pump's authoritative terminalize ENRICH a thin cancel envelope in place: a mid-run cancel writes a thin
    // Cancelled envelope, then the run's real completion supersedes the row (Cancelled is a whitelisted terminalize
    // source) and must overwrite that envelope so its terminal_status/tokens match the winning row's final status.
    private enum RunEnvelopeWriteMode
    {
        InsertIfAbsent,
        Upsert
    }

    // Correlated message update, keyed on (conversation, message, request). The guarded variant additionally requires the
    // current status to be one of the source statuses bound below, so a guarded transition (cancel / flush / terminalize)
    // is rejected atomically at the SQLite layer once the row has left the permitted set.
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
                                            content_chunk_count, reasoning_chunk_count, trace_id, started_at_utc, tool_schema_tokens, max_tool_schema_tokens, success, error_class, created_at_utc)
                                           """;

    private const string EnvelopeValues = """
                                          $id, $record_kind, $schema_version, $agent_definition_id, $conversation_id, $message_id, $invocation_id, $request_id,
                                          $model_name, $provider, $config_hash, $terminal_status, $latency_ms, $prompt_tokens, $completion_tokens, $reasoning_tokens, $total_tokens,
                                          $content_chunk_count, $reasoning_chunk_count, $trace_id, $started_at_utc, $tool_schema_tokens, $max_tool_schema_tokens, $success, $error_class, $created_at_utc
                                          """;

    // InsertIfAbsent: the WHERE NOT EXISTS on (record_kind, message_id) makes the write a no-op when the message already
    // has an envelope, so a startup-reconcile backfill or a cancel that lost the race never duplicates/clobbers one.
    private const string EnvelopeInsertIfAbsentSql = $"""
                                                      INSERT INTO agent_execution_logs
                                                          {EnvelopeColumns}
                                                      SELECT {EnvelopeValues}
                                                      WHERE NOT EXISTS (SELECT 1 FROM agent_execution_logs WHERE record_kind = $record_kind AND message_id = $message_id);
                                                      """;

    // The ChatRunEnvelope record_kind as a SQL literal. It must equal (int)AgentExecutionLogRecordKind.ChatRunEnvelope so
    // the upsert's ON CONFLICT WHERE matches the filtered unique index predicate; a const string (not the runtime cast) is
    // required so EnvelopeUpsertSql stays a compile-time constant (CA2100). If it ever drifts from the enum the ON CONFLICT
    // clause resolves against no index and SQLite throws, so the enrich-a-thin-cancel-envelope test fails loud.
    private const string EnvelopeRecordKindLiteral = "1";

    // Upsert: the pump's authoritative terminalize wins, so on a conflict with an existing envelope (a thin one a prior
    // cancel wrote) it overwrites every run-outcome column in place — keeping envelope terminal_status/tokens == the row's
    // final status. The conflict target's WHERE mirrors the filtered unique index so SQLite resolves it against that
    // partial index; the id / record_kind / message_id conflict keys are left as first written.
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
                                                  success = excluded.success,
                                                  error_class = excluded.error_class,
                                                  created_at_utc = excluded.created_at_utc;
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
            // A partial flush is a mid-stream content advance, not a conversation-level event: it fires per debounce
            // window and would otherwise run a second UPDATE (conversation touch) every time. The conversation was
            // already touched when the turn started (placeholder/queued/streaming) and is touched again at terminalize,
            // so skipping it here drops a redundant write from the hot streaming path without changing recency order.
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
            // Durable run envelope written atomically with the terminal row: both commit or roll back together.
            // Upsert: the pump's authoritative terminalize is the winning outcome, so it enriches/overwrites any thin
            // envelope a prior cancel wrote for this message (see RunEnvelopeWriteMode), keeping envelope status == row status.
            envelope: request.Envelope,
            envelopeWriteMode: RunEnvelopeWriteMode.Upsert,
            // KB sources that grounded this turn; null on paths that retrieved nothing preserves any
            // existing persisted sources, just like Parts.
            sources: request.Sources);
    }

    public async Task<NodeChatCancelResultDto> CancelMessageAsync(NodeChatCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A cancel is a terminal transition, so it writes its run envelope atomically in the same guarded UPDATE — the
        // envelope block only fires when the cancel actually transitions the row (a rejected/idempotent cancel returns
        // before it and writes nothing). This closes the gap where a cancel-before-queued/streaming left a terminal
        // Cancelled row envelope-less until the next startup reconcile. The payload is thin: there is no InvocationState
        // at cancel time, so invocation id / tokens / duration / chunk counts are unknown and omitted (mirrors the
        // interrupted terminalize); the terminal status/success and bound agent id are derived from the winning row. It
        // is InsertIfAbsent so a real envelope already present (e.g. a race where the run's terminalize committed first)
        // is never clobbered by this thinner one.
        var envelope = new AgentRunEnvelopeMetadata(InvocationId: null, DurationMs: 0L, TraceId: CurrentTraceId());

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
            envelopeWriteMode: RunEnvelopeWriteMode.InsertIfAbsent).ConfigureAwait(false);

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
                var current = await ReadMessageAsync(dbContext, correlation.ConversationId, correlation.MessageId, token).ConfigureAwait(false)
                              ?? throw new InvalidOperationException("The correlated node chat message was not found.");
                if (current.RequestId != correlation.RequestId)
                {
                    throw new InvalidOperationException("The correlated node chat request id did not match the persisted message.");
                }

                // Transition guard (cancel / flush / terminalize): a write is only allowed from one of the source statuses
                // the caller declared via NodeChatMessageTransitions. Once the row has left that set — e.g. a terminalize
                // already ran, or a cancel/flush arrives after a terminal — the update is skipped and the true current
                // state is returned unchanged. The per-message write lock makes this read authoritative; the
                // AND status IN (...) predicate below re-enforces it atomically at the SQLite layer.
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
                // Agent attribution and the reasoning effort are stamped once at placeholder/variant mint and never
                // updated here, so always preserve them from current — otherwise a later flush/terminalize would
                // re-serialize the blob without those fields and silently drop the per-response attribution.
                var metadata = SerializeMetadata(current.MetadataJson, nextReasoning, nextModel, nextInputTokens, nextOutputTokens, nextTotalTokens, nextReasoningTokens, nextParts,
                    current.AgentDefinitionId, current.AgentName, current.ReasoningEffort, nextGenerationDurationMs, nextSources);

                // When a run envelope must be written, the message UPDATE, the envelope insert, and the conversation touch
                // run in ONE transaction so the terminal row and its content-free envelope commit or roll back together
                // (no swallowed best-effort write — an envelope failure fails/retries the terminalize like
                // any persistence failure). Non-terminal updates (flush / queued / streaming) keep the prior
                // single-statement autocommit path unchanged, so the hot streaming path is untouched.
                var writeEnvelope = envelope is not null && IsTerminalStatus(nextStatus);
                await using var transaction = writeEnvelope
                    ? await dbContext.Database.BeginTransactionAsync(token).ConfigureAwait(false)
                    : null;

                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                if (transaction is not null)
                {
                    command.Transaction = transaction.GetDbTransaction();
                }

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
                    // Bind every one of the fixed IN slots. Spare slots (a set smaller than MaxSourceStatusSlots) repeat a
                    // real member rather than binding NULL, so the IN predicate stays exact without NULL-comparison subtlety.
                    var sourceStatuses = new List<string>(requiredCurrentStatuses);
                    for (var slot = 0; slot < MaxSourceStatusSlots; slot++)
                    {
                        AddParameter(command, $"$required_status_{slot}", sourceStatuses[slot < sourceStatuses.Count ? slot : sourceStatuses.Count - 1]);
                    }
                }

                await OpenIfNeededAsync(command.Connection, token).ConfigureAwait(false);
                var affected = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (requiredCurrentStatuses is not null && affected == 0)
                {
                    // The atomic predicate rejected the write because the row reached a terminal status; return the true
                    // current state without a rewrite, an envelope, or a conversation touch. An opened transaction simply
                    // disposes without a commit — nothing was written.
                    return current;
                }

                if (writeEnvelope)
                {
                    // The terminal status/success and the bound agent id are taken from THIS winning write, so the
                    // envelope can never disagree with the row. The write mode governs reconciliation with any envelope the
                    // message already has: InsertIfAbsent (cancel, backfill) keeps the first write; Upsert (the pump's
                    // authoritative terminalize) enriches/overwrites a thin cancel envelope so its fields match the final row.
                    await WriteRunEnvelopeRowAsync(dbContext,
                        transaction?.GetDbTransaction(),
                        correlation,
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
                        token).ConfigureAwait(false);
                }

                if (touchConversation)
                {
                    await TouchConversationAsync(dbContext, correlation.ConversationId, updatedAtUtc, token).ConfigureAwait(false);
                }

                if (transaction is not null)
                {
                    await transaction.CommitAsync(token).ConfigureAwait(false);
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

    // Writes the content-free durable run-envelope row for a terminalized message on the caller's raw connection, enlisted
    // in the terminalize transaction, so the envelope commits atomically with the terminal row. Metadata only: NO prompt /
    // completion / tool-argument content is written. Uses AddParameter (the same helper as the message writes) so a null
    // optional binds as SQL NULL. Two constant statements, never string-built from input; the <paramref name="writeMode" />
    // selects between them:
    //   - InsertIfAbsent — INSERT ... WHERE NOT EXISTS on (record_kind, message_id): a message already enveloped (a startup
    //     recovery backfill, or a race that terminalized first) is never duplicated or overwritten; the first write wins.
    //   - Upsert — INSERT ... ON CONFLICT(message_id) WHERE record_kind = envelope DO UPDATE: the pump's authoritative
    //     terminalize enriches/overwrites a thin cancel envelope in place so its terminal_status/tokens match the row's
    //     final status. The conflict target's WHERE mirrors the filtered unique index (ix_agent_execution_logs_envelope_
    //     message_id) so SQLite resolves it against that partial index; id / record_kind / message_id are conflict keys and
    //     are not reassigned.
    private static async Task WriteRunEnvelopeRowAsync(NodeChatDbContext dbContext,
        DbTransaction? transaction,
        NodeChatMessageCorrelation correlation,
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
        AddParameter(command, "$conversation_id", correlation.ConversationId);
        AddParameter(command, "$message_id", correlation.MessageId);
        AddParameter(command, "$invocation_id", envelope.InvocationId);
        AddParameter(command, "$request_id", correlation.RequestId);
        AddParameter(command, "$model_name", model ?? string.Empty);
        AddParameter(command, "$provider", envelope.Provider);
        AddParameter(command, "$config_hash", string.Empty);
        AddParameter(command, "$terminal_status", terminalStatus);
        AddParameter(command, "$latency_ms", envelope.DurationMs);
        AddParameter(command, "$prompt_tokens", promptTokens);
        AddParameter(command, "$completion_tokens", completionTokens);
        AddParameter(command, "$reasoning_tokens", reasoningTokens);
        AddParameter(command, "$total_tokens", totalTokens);
        AddParameter(command, "$content_chunk_count", envelope.ContentChunkCount);
        AddParameter(command, "$reasoning_chunk_count", envelope.ReasoningChunkCount);
        AddParameter(command, "$trace_id", envelope.TraceId);
        AddParameter(command, "$started_at_utc", envelope.StartedAtUtc);
        AddParameter(command, "$tool_schema_tokens", envelope.ToolSchemaTokens);
        AddParameter(command, "$max_tool_schema_tokens", envelope.MaxToolSchemaTokens);
        AddParameter(command, "$success", string.Equals(terminalStatus, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal) ? 1 : 0);
        AddParameter(command, "$error_class", envelope.FailureCategory);
        AddParameter(command, "$created_at_utc", createdAtUtc);

        await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // W3C trace id of the ambient activity (for cross-correlation with exported traces), or null when no activity is in
    // scope. A default (all-zero) id is treated as absent. Mirrors the pump's helper so a cancel-written thin envelope
    // carries the same best-effort correlation the interrupted terminalize does.
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
