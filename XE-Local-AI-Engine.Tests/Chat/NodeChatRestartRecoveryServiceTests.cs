namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class NodeChatRestartRecoveryServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_MarksPendingAndStreamingAssistantMessagesInterrupted()
    {
        await using var provider = await BuildProviderAsync("restart-recovery.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Restart", UserId = "node", CreatedAtUtc = 10 });
        var pendingCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        var streamingCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 12);
        var completedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 13);
        var cancelledCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 14);
        var failedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 15);
        var interruptedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 16);

        await persistence.MarkAssistantStreamingAsync(streamingCorrelation, updatedAtUtc: 20);
        await persistence.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest { Correlation = streamingCorrelation, Content = "partial answer", Reasoning = "partial reasoning", UpdatedAtUtc = 21 });
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest { Correlation = completedCorrelation, Status = NodeChatMessageStatusValues.Completed, UpdatedAtUtc = 22, Content = "done" });
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest { Correlation = cancelledCorrelation, Status = NodeChatMessageStatusValues.Cancelled, UpdatedAtUtc = 23 });
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest { Correlation = failedCorrelation, Status = NodeChatMessageStatusValues.Failed, UpdatedAtUtc = 24, Error = "provider failed" });
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = interruptedCorrelation,
            Status = NodeChatMessageStatusValues.Interrupted,
            UpdatedAtUtc = 25,
            Error = "already interrupted"
        });

        var recoveredCount = await recovery.RecoverInterruptedMessagesAsync(99);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId));
        var pending = loaded.Messages.Single(message => message.MessageId == pendingCorrelation.MessageId);
        var streaming = loaded.Messages.Single(message => message.MessageId == streamingCorrelation.MessageId);
        var completed = loaded.Messages.Single(message => message.MessageId == completedCorrelation.MessageId);
        var cancelled = loaded.Messages.Single(message => message.MessageId == cancelledCorrelation.MessageId);
        var failed = loaded.Messages.Single(message => message.MessageId == failedCorrelation.MessageId);
        var interrupted = loaded.Messages.Single(message => message.MessageId == interruptedCorrelation.MessageId);

        AssertEx.Equal(expected: 2, recoveredCount);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, pending.Status);
        AssertEx.Equal(expected: 99L, pending.UpdatedAtUtc);
        AssertEx.Equal(NodeChatRestartRecoveryService.RestartInterruptedError, pending.Error);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, streaming.Status);
        AssertEx.Equal("partial answer", streaming.Content);
        AssertEx.Equal("partial reasoning", streaming.Reasoning);
        AssertEx.Equal(expected: 99L, streaming.UpdatedAtUtc);
        AssertEx.Equal(NodeChatRestartRecoveryService.RestartInterruptedError, streaming.Error);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, completed.Status);
        AssertEx.Equal(expected: 22L, completed.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, cancelled.Status);
        AssertEx.Equal(expected: 23L, cancelled.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Failed, failed.Status);
        AssertEx.Equal("provider failed", failed.Error);
        AssertEx.Equal(expected: 24L, failed.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, interrupted.Status);
        AssertEx.Equal("already interrupted", interrupted.Error);
        AssertEx.Equal(expected: 25L, interrupted.UpdatedAtUtc);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_TerminalizesQueuedAndRemoteOriginAssistantMessages()
    {
        await using var provider = await BuildProviderAsync("restart-recovery-remote-queued.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);

        // An Origin=Remote conversation whose assistant placeholder is stuck in `queued` (the state held before
        // the collision lease is acquired) plus a Remote streaming row — both must be terminalized. A Remote
        // completed row must be left alone. Recovery filters by role+status only, so Origin never excludes a row.
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Remote", UserId = "node", CreatedAtUtc = 40, Origin = NodeChatOriginValues.Remote });
        var queuedCorrelation = await CreateRemoteAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 41);
        var streamingCorrelation = await CreateRemoteAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 42);
        var completedCorrelation = await CreateRemoteAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 43);

        await persistence.MarkAssistantQueuedAsync(queuedCorrelation, updatedAtUtc: 44);
        await persistence.MarkAssistantStreamingAsync(streamingCorrelation, updatedAtUtc: 45);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest { Correlation = completedCorrelation, Status = NodeChatMessageStatusValues.Completed, UpdatedAtUtc = 46, Content = "done" });

        var recoveredCount = await recovery.RecoverInterruptedMessagesAsync(99);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId));
        var queued = loaded.Messages.Single(message => message.MessageId == queuedCorrelation.MessageId);
        var streaming = loaded.Messages.Single(message => message.MessageId == streamingCorrelation.MessageId);
        var completed = loaded.Messages.Single(message => message.MessageId == completedCorrelation.MessageId);

        AssertEx.Equal(expected: 2, recoveredCount);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, queued.Status);
        AssertEx.Equal(NodeChatOriginValues.Remote, queued.Origin);
        AssertEx.Equal(expected: 99L, queued.UpdatedAtUtc);
        AssertEx.Equal(NodeChatRestartRecoveryService.RestartInterruptedError, queued.Error);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, streaming.Status);
        AssertEx.Equal(NodeChatOriginValues.Remote, streaming.Origin);
        AssertEx.Equal(expected: 99L, streaming.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, completed.Status);
        AssertEx.Equal(expected: 46L, completed.UpdatedAtUtc);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_ReturnsZeroWhenNoNonterminalMessagesExist()
    {
        await using var provider = await BuildProviderAsync("restart-recovery-empty.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "No recovery", UserId = null, CreatedAtUtc = 30 });
        var userMessage = await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest { ConversationId = conversation.ConversationId, MessageId = Guid.NewGuid(), Content = "hello", CreatedAtUtc = 31 });

        var recoveredCount = await recovery.RecoverInterruptedMessagesAsync(100);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId));
        var loadedUserMessage = loaded.Messages.Single(message => message.MessageId == userMessage.MessageId);

        AssertEx.Equal(expected: 0, recoveredCount);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, loadedUserMessage.Status);
        AssertEx.Equal(expected: 31L, loadedUserMessage.UpdatedAtUtc);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_ReconcilesEnvelopesForEveryTerminalStateLackingOne()
    {
        await using var provider = await BuildProviderAsync("restart-recovery-envelope-reconcile.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Reconcile", UserId = "node", CreatedAtUtc = 10 });

        // Two non-terminal rows (crash before terminal) plus three rows terminalized WITHOUT an envelope (a crash / write
        // failure after the terminal commit). Recovery must reconcile ALL FIVE, preserving each row's persisted status.
        var pendingCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        var streamingCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 12);
        var completedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 13);
        var failedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 14);
        var cancelledCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 15);
        await persistence.MarkAssistantStreamingAsync(streamingCorrelation, updatedAtUtc: 20);
        // No Envelope on these terminalize calls → the atomic write does not run, mimicking the crash gap the reconcile closes.
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest { Correlation = completedCorrelation, Status = NodeChatMessageStatusValues.Completed, UpdatedAtUtc = 21, Content = "done" });
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest { Correlation = failedCorrelation, Status = NodeChatMessageStatusValues.Failed, UpdatedAtUtc = 22, Error = "boom" });
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest { Correlation = cancelledCorrelation, Status = NodeChatMessageStatusValues.Cancelled, UpdatedAtUtc = 23 });

        _ = await recovery.RecoverInterruptedMessagesAsync(99);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 5, envelopes.Count);

        // Each backfilled envelope's terminal status mirrors the message's persisted status, and success only for completed.
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, EnvelopeFor(envelopes, pendingCorrelation).TerminalStatus);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, EnvelopeFor(envelopes, streamingCorrelation).TerminalStatus);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, EnvelopeFor(envelopes, completedCorrelation).TerminalStatus);
        AssertEx.True(EnvelopeFor(envelopes, completedCorrelation).Success, "A reconciled completed envelope must be marked successful.");
        AssertEx.Equal(NodeChatMessageStatusValues.Failed, EnvelopeFor(envelopes, failedCorrelation).TerminalStatus);
        AssertEx.False(EnvelopeFor(envelopes, failedCorrelation).Success);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, EnvelopeFor(envelopes, cancelledCorrelation).TerminalStatus);
        AssertEx.False(EnvelopeFor(envelopes, cancelledCorrelation).Success);
        // Correlation carried through from the message row.
        AssertEx.Equal(pendingCorrelation.RequestId, EnvelopeFor(envelopes, pendingCorrelation).RequestId);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_BackfillIsIdempotent_NoDuplicateEnvelopes()
    {
        await using var provider = await BuildProviderAsync("restart-recovery-envelope-idempotent.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Backfill twice", UserId = "node", CreatedAtUtc = 10 });
        _ = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);

        // Running recovery twice (two restarts) must never duplicate an envelope: the NOT EXISTS guard plus the filtered
        // unique index keep the backfill idempotent.
        _ = await recovery.RecoverInterruptedMessagesAsync(99);
        _ = await recovery.RecoverInterruptedMessagesAsync(100);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, envelopes.Count);
    }

    [Test]
    public async Task TerminalizeAssistantMessageAsync_WithEnvelope_WritesEnvelopeAtomicallyWithBoundAgentId()
    {
        await using var provider = await BuildProviderAsync("terminalize-atomic-envelope.sqlite");
        var persistence = CreatePersistenceService(provider);
        var agentId = Guid.NewGuid();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Atomic", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11, agentDefinitionId: agentId);
        var invocationId = Guid.NewGuid();

        var persisted = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 20,
            Content = "answer",
            Model = "llama-3.1",
            InputCount = 100,
            OutputCount = 25,
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = invocationId, DurationMs = 1500L, ContentChunkCount = 8 }
        });

        AssertEx.Equal(NodeChatMessageStatusValues.Completed, persisted.Status);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, envelopes.Count);
        var envelope = envelopes[0];
        // Envelope written in the same operation as the terminal row, with the winning status and the bound agent id.
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, envelope.TerminalStatus);
        AssertEx.True(envelope.Success);
        AssertEx.Equal(agentId, envelope.AgentDefinitionId);
        AssertEx.Equal(correlation.MessageId, envelope.MessageId);
        AssertEx.Equal(invocationId, envelope.InvocationId);
        AssertEx.Equal(expected: 1500L, envelope.DurationMs);
        AssertEx.Equal(expected: 100, envelope.PromptTokens);
        AssertEx.Equal(expected: 25, envelope.CompletionTokens);
        AssertEx.Equal(expected: 8, envelope.ContentChunkCount);
    }

    [Test]
    public async Task TerminalizeAssistantMessageAsync_WhenAlreadyTerminal_DoesNotWriteASecondEnvelope()
    {
        await using var provider = await BuildProviderAsync("terminalize-idempotent-envelope.sqlite");
        var persistence = CreatePersistenceService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Idempotent", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 20,
            Content = "answer",
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = Guid.NewGuid(), DurationMs = 10L }
        });
        // A second terminalize is guard-rejected by the transition table, so it must not write a second envelope.
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Failed,
            UpdatedAtUtc = 21,
            Error = "late",
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = Guid.NewGuid(), DurationMs = 0L }
        });

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, envelopes.Count);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, envelopes[0].TerminalStatus);
    }

    [Test]
    public async Task CancelMessageAsync_WhenTransitions_WritesThinEnvelopeAtomically()
    {
        await using var provider = await BuildProviderAsync("cancel-writes-thin-envelope.sqlite");
        var persistence = CreatePersistenceService(provider);
        var agentId = Guid.NewGuid();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "CancelEnvelope", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11, agentDefinitionId: agentId);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 12);

        // A cancel that transitions the row writes its (thin) envelope in the same guarded UPDATE. No InvocationState exists
        // at cancel time, so tokens/invocation id/duration are absent, but the envelope is present immediately.
        var cancel = await persistence.CancelMessageAsync(new NodeChatCancelRequest { Correlation = correlation, CancelledAtUtc = 13 });
        AssertEx.True(cancel.Cancelled);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, envelopes.Count);
        var envelope = envelopes[0];
        AssertEx.Equal(correlation.MessageId, envelope.MessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, envelope.TerminalStatus);
        AssertEx.False(envelope.Success);
        // The bound agent id and correlation are carried from the winning row; run detail (invocation id/tokens) is absent.
        AssertEx.Equal(agentId, envelope.AgentDefinitionId);
        AssertEx.Equal(correlation.RequestId, envelope.RequestId);
        AssertEx.Null(envelope.InvocationId);
    }

    [Test]
    public async Task CancelThenAuthoritativeCompletion_EnrichesThinEnvelopeToWinningStatus()
    {
        await using var provider = await BuildProviderAsync("cancel-then-complete-envelope.sqlite");
        var persistence = CreatePersistenceService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "MidRunCancel", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 12);

        // Mid-run cancel wins the row first, writing a thin Cancelled envelope.
        await persistence.CancelMessageAsync(new NodeChatCancelRequest { Correlation = correlation, CancelledAtUtc = 13 });
        var afterCancel = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, afterCancel.Count);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, afterCancel[0].TerminalStatus);

        // The run actually completes: the pump's authoritative terminalize supersedes the Cancelled row AND (option b)
        // UPSERTs the envelope in place, so the single envelope now reflects the real completed outcome — never a stale thin
        // Cancelled envelope over a Completed row. The envelope terminal status equals the row's final status (the invariant).
        var invocationId = Guid.NewGuid();
        var completed = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 14,
            Content = "the real answer",
            Model = "llama-3.1",
            InputCount = 100,
            OutputCount = 25,
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = invocationId, DurationMs = 1500L, ContentChunkCount = 8 }
        });
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, completed.Status);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, envelopes.Count);
        var envelope = envelopes[0];
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, envelope.TerminalStatus);
        AssertEx.True(envelope.Success);
        AssertEx.Equal(invocationId, envelope.InvocationId);
        AssertEx.Equal(expected: 1500L, envelope.DurationMs);
        AssertEx.Equal(expected: 100, envelope.PromptTokens);
        AssertEx.Equal(expected: 25, envelope.CompletionTokens);
        AssertEx.Equal(expected: 8, envelope.ContentChunkCount);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_BackfillsEnvelopesAtTheCurrentVersionWithNullTokenColumns()
    {
        // The backfill stamps the CURRENT schema version even though it supplies no generation detail: a version says
        // the field set MAY include those columns, never that they are populated. A backfilled row therefore declares
        // the current version and leaves both token columns null.
        await using var provider = await BuildProviderAsync("restart-recovery-envelope-version.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Version", UserId = "node", CreatedAtUtc = 10 });
        _ = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);

        _ = await recovery.RecoverInterruptedMessagesAsync(99);

        var envelope = (await ReadEnvelopesAsync(provider, conversation.ConversationId)).Single();
        AssertEx.Equal(AgentRunEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
        AssertEx.Null(envelope.ToolSchemaTokens);
        AssertEx.Null(envelope.MaxToolSchemaTokens);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_BackfillsEnvelopesAtTheCurrentVersionWithNullDispatchColumns()
    {
        // Same rule as every other generation-detail column: the backfill stamps the CURRENT version (now v5) and
        // leaves the dispatch labels null, because it has no invocation state to read a dispatch from.
        await using var provider = await BuildProviderAsync("restart-recovery-envelope-dispatch.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Dispatch", UserId = "node", CreatedAtUtc = 10 });
        _ = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);

        _ = await recovery.RecoverInterruptedMessagesAsync(99);

        var envelope = (await ReadEnvelopesAsync(provider, conversation.ConversationId)).Single();
        AssertEx.Equal(AgentRunEnvelope.CurrentSchemaVersion, envelope.SchemaVersion);
        AssertEx.Null(envelope.DispatchedTier);
        AssertEx.Null(envelope.AuthoredEffort);
    }

    [Test]
    public async Task CancelThenAuthoritativeCompletion_UpsertsTheDispatchLabelsOverTheThinNulls()
    {
        // The end-to-end pin for the two write statements: the thin cancel's InsertIfAbsent writes both columns as
        // null and the authoritative Upsert overwrites them in place. A column or parameter missing from either
        // statement fails here rather than at runtime.
        await using var provider = await BuildProviderAsync("cancel-then-complete-dispatch.sqlite");
        var persistence = CreatePersistenceService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "UpsertDispatch", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 12);

        await persistence.CancelMessageAsync(new NodeChatCancelRequest { Correlation = correlation, CancelledAtUtc = 13 });
        var thin = (await ReadEnvelopesAsync(provider, conversation.ConversationId)).Single();
        AssertEx.Null(thin.DispatchedTier, "The thin cancel envelope has no invocation state to read a dispatch from.");

        _ = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 14,
            Content = "the real answer",
            Envelope = new AgentRunEnvelopeMetadata
                                 {
                                     InvocationId = Guid.NewGuid(),
                                     DurationMs = 1_500L,
                                     DispatchedTier = "deep",
                                     AuthoredEffort = "auto"
                                 }
        });

        var envelope = (await ReadEnvelopesAsync(provider, conversation.ConversationId)).Single();
        AssertEx.Equal("deep", envelope.DispatchedTier);
        AssertEx.Equal("auto", envelope.AuthoredEffort);
    }

    [Test]
    public async Task CancelThenAuthoritativeCompletion_UpsertsTheToolSchemaTokenEstimateOverTheThinNulls()
    {
        // The two write modes share one column list, so the thin cancel's InsertIfAbsent writes both columns as null and
        // the pump's authoritative Upsert then overwrites them in place — the same "the terminalize wins" rule every
        // other run-outcome column follows, so the estimate can never be stranded on a superseded thin envelope.
        await using var provider = await BuildProviderAsync("cancel-then-complete-tool-schema-tokens.sqlite");
        var persistence = CreatePersistenceService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "UpsertTokens", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 12);

        await persistence.CancelMessageAsync(new NodeChatCancelRequest { Correlation = correlation, CancelledAtUtc = 13 });
        var thin = (await ReadEnvelopesAsync(provider, conversation.ConversationId)).Single();
        AssertEx.Null(thin.ToolSchemaTokens, "The thin cancel envelope has no invocation state to read an estimate from.");

        _ = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 14,
            Content = "the real answer",
            Envelope = new AgentRunEnvelopeMetadata
                                 {
                                     InvocationId = Guid.NewGuid(),
                                     DurationMs = 1_500L,
                                     ToolSchemaTokens = 9_001L,
                                     MaxToolSchemaTokens = 512
                                 }
        });

        var envelope = (await ReadEnvelopesAsync(provider, conversation.ConversationId)).Single();
        AssertEx.Equal(expected: 9_001L, envelope.ToolSchemaTokens);
        AssertEx.Equal(expected: 512, envelope.MaxToolSchemaTokens);
    }

    [Test]
    public async Task CancelThenInterrupted_LeavesCancelledEnvelope_PreservingTheInvariant()
    {
        await using var provider = await BuildProviderAsync("cancel-then-interrupt-envelope.sqlite");
        var persistence = CreatePersistenceService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "CancelThenInterrupt", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 12);
        await persistence.CancelMessageAsync(new NodeChatCancelRequest { Correlation = correlation, CancelledAtUtc = 13 });

        // Interrupted is NOT whitelisted to supersede Cancelled, so the terminalize message UPDATE is a guard-rejected no-op
        // and its envelope write never runs. The row stays Cancelled and the envelope stays Cancelled: status equality holds.
        var interrupted = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Interrupted,
            UpdatedAtUtc = 14,
            Error = "stream lost",
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = Guid.NewGuid(), DurationMs = 0L }
        });
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, interrupted.Status);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, envelopes.Count);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, envelopes[0].TerminalStatus);
        AssertEx.Null(envelopes[0].InvocationId);
    }

    [Test]
    public async Task CancelMessageAsync_WhenAlreadyTerminal_WritesNoEnvelope()
    {
        await using var provider = await BuildProviderAsync("cancel-after-terminal-envelope.sqlite");
        var persistence = CreatePersistenceService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "CancelAfterTerminal", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 12);
        var completionInvocationId = Guid.NewGuid();
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 13,
            Content = "done",
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = completionInvocationId, DurationMs = 42L }
        });

        // A cancel that races a completed terminalize is guard-rejected (the row already left the cancellable set), so it
        // writes no envelope: the completed envelope stands unchanged and is never clobbered by a thin cancel one.
        var cancel = await persistence.CancelMessageAsync(new NodeChatCancelRequest { Correlation = correlation, CancelledAtUtc = 14 });
        AssertEx.False(cancel.Cancelled);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, cancel.Status);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Equal(expected: 1, envelopes.Count);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, envelopes[0].TerminalStatus);
        AssertEx.Equal(completionInvocationId, envelopes[0].InvocationId);
    }

    [Test]
    public async Task ConversationPurge_ThenRecovery_LeavesNoOrphanEnvelope()
    {
        await using var provider = await BuildProviderAsync("purge-race-no-orphan.sqlite");
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "PurgeRace", UserId = "node", CreatedAtUtc = 10 });
        var correlation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, createdAtUtc: 11);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 20,
            Content = "answer",
            Envelope = new AgentRunEnvelopeMetadata { InvocationId = Guid.NewGuid(), DurationMs = 5L }
        });

        // Purge deletes the message row AND its envelope. A subsequent reconcile selects FROM messages, so with the row
        // gone it inserts nothing — a late envelope can never orphan the conversation's plaintext correlation.
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            await ConversationFootprintPurge.DeleteAsync(dbContext, conversation.ConversationId, CancellationToken.None);
        }

        _ = await recovery.RecoverInterruptedMessagesAsync(99);

        var envelopes = await ReadEnvelopesAsync(provider, conversation.ConversationId);
        AssertEx.Empty(envelopes);
    }

    private static AgentRunEnvelopeRecord EnvelopeFor(IReadOnlyList<AgentRunEnvelopeRecord> envelopes, NodeChatMessageCorrelation correlation)
    {
        return envelopes.Single(envelope => envelope.MessageId == correlation.MessageId);
    }

    private static async Task<IReadOnlyList<AgentRunEnvelopeRecord>> ReadEnvelopesAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var store = new AgentExecutionLogStore(dbContext, TimeProvider.System);
        return await store.ListRunEnvelopesAsync(conversationId, limit: 100);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        var databasePath = GetDatabasePath(fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        return provider;
    }

    private static NodeChatPersistenceService CreatePersistenceService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private static NodeChatRestartRecoveryService CreateRecoveryService(ServiceProvider provider)
    {
        return new NodeChatRestartRecoveryService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private static async Task<NodeChatMessageCorrelation> CreateAssistantPlaceholderAsync(NodeChatPersistenceService persistence,
        Guid conversationId,
        long createdAtUtc,
        Guid? agentDefinitionId = null)
    {
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest { ConversationId = conversationId, MessageId = messageId, RequestId = requestId, CreatedAtUtc = createdAtUtc, AgentDefinitionId = agentDefinitionId });
        return new NodeChatMessageCorrelation(conversationId, messageId, requestId);
    }

    private static async Task<NodeChatMessageCorrelation> CreateRemoteAssistantPlaceholderAsync(NodeChatPersistenceService persistence,
        Guid conversationId,
        long createdAtUtc)
    {
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = conversationId,
            MessageId = messageId,
            RequestId = requestId,
            CreatedAtUtc = createdAtUtc,
            Origin = NodeChatOriginValues.Remote
        });
        return new NodeChatMessageCorrelation(conversationId, messageId, requestId);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
