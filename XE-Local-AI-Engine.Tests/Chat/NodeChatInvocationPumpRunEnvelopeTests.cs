namespace XE_Local_AI_Engine.Tests.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

// The durable run ledger: NodeChatInvocationPump no longer writes the envelope itself — it hands the
// content-free envelope metadata INTO the terminalize persistence request so the row is written atomically with the
// terminal message row. These tests pin the pump's field mapping onto that request and that the result reflects the
// persisted winning status; the atomic write itself (agent id, winning status, idempotency) is covered by the
// persistence/recovery integration tests.
public sealed class NodeChatInvocationPumpRunEnvelopeTests
{
    [Test]
    public async Task TerminalizeAsync_CompletedRun_PassesBoundedEnvelopeMetadataIntoTerminalize()
    {
        var persistence = CreatePersistence();
        // The resolver attributes this turn to "local"; the pump must thread that onto the envelope metadata it hands to
        // the terminalize command (the row-level round-trip is covered by the envelope-transaction integration test).
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);

        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversationId, messageId, requestId);

        var state = new InvocationState
        {
            InvocationId = invocationId,
            ConversationId = conversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1",
            InputTokens = 100,
            OutputTokens = 25,
            ReasoningTokens = 5,
            TotalTokens = 130,
            StreamedChunkCount = 8,
            StreamedThinkingChunkCount = 3,
            GenerationDurationMs = 1500,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(7000)
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Status == "completed"
                && request.Model == "llama-3.1"
                && request.InputCount == 100
                && request.OutputCount == 25
                && request.Envelope != null
                && request.Envelope.InvocationId == invocationId
                && request.Envelope.DurationMs == 1500L
                && request.Envelope.ContentChunkCount == 8
                && request.Envelope.ReasoningChunkCount == 3
                && request.Envelope.StartedAtUtc == 7000L
                && request.Envelope.Provider == AgentUsageProviders.Local
                && request.Envelope.FailureCategory == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_CarriesTheToolSchemaTokenEstimateFromTheInvocationState()
    {
        // The runner reads the estimate off the provider-call budget and reports it onto the invocation state; the pump's
        // only job is to copy it onto the envelope metadata, unchanged and unrounded. The cumulative value is a long at
        // its source, so a value above int.MaxValue must survive this hop intact.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        const long wideEstimate = (long)int.MaxValue + 1;

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1",
            ToolSchemaTokens = wideEstimate,
            MaxToolSchemaTokens = 4_096
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Envelope != null
                && request.Envelope.ToolSchemaTokens == wideEstimate
                && request.Envelope.MaxToolSchemaTokens == 4_096),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_CarriesTheDispatchedTierFromTheInvocationState()
    {
        // The runner reports what `auto` resolved to onto the invocation state; the pump's only job is to copy both
        // labels onto the envelope metadata, unchanged.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1",
            DispatchedTier = "fast",
            AuthoredEffort = "auto"
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Envelope != null
                && request.Envelope.DispatchedTier == "fast"
                && request.Envelope.AuthoredEffort == "auto"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_WhenTheTurnWasNotAuto_LeavesTheDispatchColumnsNull()
    {
        // Only an `auto` turn reports a dispatch, so an ordinary turn's envelope carries nulls — which is what makes
        // `authored_effort IS NULL` the pre-`auto` population of the measurement.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1"
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Envelope != null
                && request.Envelope.DispatchedTier == null
                && request.Envelope.AuthoredEffort == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_CarriesTheModelReadinessDurationFromTheInvocationState()
    {
        // The whole-turn duration includes the local runtime's launch and model load, so the envelope has to carry the
        // warm's own duration beside it: latency_ms minus this is the warm-equivalent turn time, and without the pair a
        // cold arm and a warm arm cannot be compared at all.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1",
            GenerationDurationMs = 206_273,
            ModelReadinessMs = 178_576
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Envelope != null
                && request.Envelope.DurationMs == 206_273L
                && request.Envelope.ModelReadinessMs == 178_576L),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_WhenNoLocalRuntimeWarmed_LeavesTheModelReadinessDurationNull()
    {
        // A remote or already-warm turn measured no readiness, and null is what says so. Zero would read as "this turn
        // proved a warm start", which is the one thing an unmeasured turn must not claim.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "gpt-4o-mini",
            GenerationDurationMs = 1_500
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Envelope != null
                && request.Envelope.ModelReadinessMs == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_CarriesTheTurnTotalsOnTheEnvelopeAndTheLastRoundOnTheMessage()
    {
        // The two rows answer different questions and must not be filled from the same number. The message keeps the
        // LAST provider round's counts, which the chat context meter reads as the model's context occupancy; the
        // envelope keeps the turn's SUM over its rounds, which is what the turn cost. Summing on the message showed a
        // three-round turn as 10,722 tokens of context it never held.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1",
            InputTokens = 3_000,
            OutputTokens = 30,
            TotalTokens = 3_038,
            ReasoningTokens = 8,
            TurnInputTokens = 6_000,
            TurnOutputTokens = 60,
            TurnTotalTokens = 6_078,
            TurnReasoningTokens = 18
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.InputCount == 3_000
                && request.OutputCount == 30
                && request.TotalCount == 3_038
                && request.ReasoningCount == 8
                && request.Envelope != null
                && request.Envelope.TurnInputTokens == 6_000
                && request.Envelope.TurnOutputTokens == 60
                && request.Envelope.TurnTotalTokens == 6_078
                && request.Envelope.TurnReasoningTokens == 18),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_WhenNoTurnTotalsWereReported_LeavesThemNullSoTheEnvelopeFallsBackToTheMessage()
    {
        // The platform path and the restart-recovery backfill report no turn totals. Null is what tells the envelope
        // write to keep using the message's own tokens, so those rows read exactly as they always have.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence, AgentUsageProviders.Local);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1",
            InputTokens = 3_000,
            OutputTokens = 30
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Envelope != null
                && request.Envelope.TurnInputTokens == null
                && request.Envelope.TurnOutputTokens == null
                && request.Envelope.TurnTotalTokens == null
                && request.Envelope.TurnReasoningTokens == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeInterruptedAsync_LeavesTheToolSchemaTokenEstimateNull()
    {
        // The interrupted/thin path has no invocation state to read the counters from, so the columns stay null rather
        // than being manufactured as zero — which is what lets a reader tell "not measured" from "measured as none".
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence);
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        _ = await pump.TerminalizeInterruptedAsync(correlation, new NodeChatPumpCursor("partial", string.Empty), wasCancelled: false);

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Envelope != null
                && request.Envelope.ToolSchemaTokens == null
                && request.Envelope.MaxToolSchemaTokens == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_FailedRun_PassesFailureCategoryInEnvelopeMetadata()
    {
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Failed,
            ModelUsed = "llama-3.1",
            FailureCategory = FailureCategory.ProviderUnreachable,
            GenerationDurationMs = 42
        };

        _ = await pump.TerminalizeAsync(correlation, state, requestedModel: null);

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Status == "failed"
                && request.Envelope != null
                && request.Envelope.FailureCategory == "ProviderUnreachable"
                && request.Envelope.DurationMs == 42L),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_CancelledRun_PersistsNullErrorSoNoBannerShows()
    {
        // A user cancel (or operator eject, also Cancelled-category) is an outcome, not a failure: the terminal row
        // must carry NO error text, so the chat never shows a red error banner for a cancelled turn.
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Cancelled,
            FailureCategory = FailureCategory.Cancelled,
            Error = "Invocation timed out or was cancelled",
            ModelUsed = "llama-3.1"
        };

        _ = await pump.TerminalizeAsync(correlation, state, requestedModel: null);

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request => request.Status == "cancelled" && request.Error == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_FailedRun_KeepsClassifiedErrorText()
    {
        // A genuine failure keeps its classified, user-safe message on the row (only cancellations are nulled).
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Failed,
            FailureCategory = FailureCategory.ProviderUnreachable,
            Error = "Provider unreachable.",
            ModelUsed = "llama-3.1"
        };

        _ = await pump.TerminalizeAsync(correlation, state, requestedModel: null);

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request => request.Status == "failed" && request.Error == "Provider unreachable."),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_WhenNoRunnerDuration_FallsBackToStartToCompleteElapsed()
    {
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1",
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(750)
            // GenerationDurationMs deliberately left null (legacy/platform turn).
        };

        _ = await pump.TerminalizeAsync(correlation, state, requestedModel: null);

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request => request.Envelope != null && request.Envelope.DurationMs == 750L),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeInterruptedAsync_PassesThinEnvelopeMetadataIntoTerminalize()
    {
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cursor = new NodeChatPumpCursor("partial", string.Empty);

        _ = await pump.TerminalizeInterruptedAsync(correlation, cursor, wasCancelled: false);

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Status == "interrupted"
                && request.Envelope != null
                && request.Envelope.InvocationId == null
                && request.Envelope.DurationMs == 0L),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeInterruptedAsync_WhenCancelled_PassesCancelledStatus()
    {
        var persistence = CreatePersistence();
        var pump = ChatPumpTestFactory.Create(persistence);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        _ = await pump.TerminalizeInterruptedAsync(correlation, new NodeChatPumpCursor(string.Empty, string.Empty), wasCancelled: true);

        // A user-cancelled interrupted stream also persists NO error text, so no banner shows.
        await persistence.Received(1).TerminalizeAssistantMessageAsync(
            Arg.Is<NodeChatTerminalizeMessageRequest>(request => request.Status == "cancelled" && request.Error == null && request.Envelope != null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeInterruptedAsync_WhenPersistedStatusWins_ResultReflectsPersisted()
    {
        // Simulate the transition guard rejecting an Interrupted write against an already-Cancelled row: the persistence
        // seam returns the Cancelled winning row. The returned status and event type must reflect that persisted state
        // rather than the requested Interrupted (the envelope's own status is derived inside the terminalize command).
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.TerminalizeAssistantMessageAsync(Arg.Any<NodeChatTerminalizeMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(new NodeChatPersistedMessageDto(correlation.MessageId,
                       correlation.ConversationId,
                       correlation.RequestId,
                       Sequence: 0,
                       "assistant",
                       "partial",
                       Reasoning: null,
                       NodeChatMessageStatusValues.Cancelled,
                       CreatedAtUtc: 0,
                       UpdatedAtUtc: 5,
                       Model: null,
                       Error: null,
                       MetadataJson: null));
        var pump = ChatPumpTestFactory.Create(persistence);

        var result = await pump.TerminalizeInterruptedAsync(correlation, new NodeChatPumpCursor("partial", string.Empty), wasCancelled: false);

        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, result.TerminalStatus);
        AssertEx.Equal(ChatStreamEventTypes.AssistantCancelled, result.EventType);
    }

    [Test]
    public async Task TerminalizeAsync_WhenAnAutoSwapServedTheTurn_AttributesBothTheModelAndTheProviderToTheServedModel()
    {
        // The runner records the served model on the invocation state after an admitted `auto` model swap. Everything
        // downstream has to follow it: the persisted message row's model, and the provider the envelope is attributed
        // to — which is looked up FROM that same model id, not from the model the request asked for. Attributing the
        // fast model's tokens to the big model's provider is exactly what makes the measurement queries lie.
        var persistence = CreatePersistence();
        var resolver = Substitute.For<IUsageProviderResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(string.Equals(callInfo.Arg<string?>(), "qwen3-1.7b", StringComparison.Ordinal)
                    ? AgentUsageProviders.Local
                    : AgentUsageProviders.Unknown));
        var pump = new NodeChatInvocationPump(persistence, resolver, TimeProvider.System);

        var conversationId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversationId, Guid.NewGuid(), Guid.NewGuid());
        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "qwen3-1.7b",
            DispatchedTier = "fast",
            AuthoredEffort = "auto",
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(7000)
        };

        _ = await pump.TerminalizeAsync(correlation, state, "qwen3.8-27b");

        await persistence.Received(1).TerminalizeAssistantMessageAsync(Arg.Is<NodeChatTerminalizeMessageRequest>(request =>
                request.Model == "qwen3-1.7b"
                && request.Envelope != null
                && request.Envelope.Provider == AgentUsageProviders.Local
                && request.Envelope.DispatchedTier == "fast"
                && request.Envelope.AuthoredEffort == "auto"),
            Arg.Any<CancellationToken>());
    }

    private static INodeChatPersistenceService CreatePersistence()
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        // The pump derives its result from the PERSISTED winning row, so the terminalize seam must return a message. Echo
        // the requested terminal (the happy-path winning status); the transition-table rejection path is covered directly
        // in the persistence/recovery integration tests.
        persistence.TerminalizeAssistantMessageAsync(Arg.Any<NodeChatTerminalizeMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.Arg<NodeChatTerminalizeMessageRequest>();
                       return new NodeChatPersistedMessageDto(request.Correlation.MessageId,
                           request.Correlation.ConversationId,
                           request.Correlation.RequestId,
                           Sequence: 0,
                           "assistant",
                           request.Content ?? string.Empty,
                           request.Reasoning,
                           request.Status,
                           CreatedAtUtc: 0,
                           request.UpdatedAtUtc,
                           request.Model,
                           request.Error,
                           MetadataJson: null);
                   });
        return persistence;
    }
}
