namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Contracts.Enums;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

// The durable run ledger (MED-007): NodeChatInvocationPump appends exactly one content-free run-envelope row per
// terminalized invocation through IAgentExecutionLogStore, for every terminal outcome (completed / failed / cancelled /
// interrupted). These tests pin the seam wiring and the field mapping; the store round-trip / schema / retention are
// covered in AdaptiveAgentMemoryStoreTests.
public sealed class NodeChatInvocationPumpRunEnvelopeTests
{
    [Test]
    public async Task TerminalizeAsync_CompletedRun_WritesExactlyOneRunEnvelopeWithBoundedFields()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        var pump = CreatePump(store);

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
            StreamedChunkCount = 8,
            StreamedThinkingChunkCount = 3,
            GenerationDurationMs = 1500
        };

        _ = await pump.TerminalizeAsync(correlation, state, "requested-model");

        await store.Received(1).AddRunEnvelopeAsync(
            Arg.Is<AgentRunEnvelopeInput>(input =>
                input.TerminalStatus == "completed"
                && input.Success
                && input.ConversationId == conversationId
                && input.MessageId == messageId
                && input.RequestId == requestId
                && input.InvocationId == invocationId
                && input.ModelName == "llama-3.1"
                && input.DurationMs == 1500L
                && input.PromptTokens == 100
                && input.CompletionTokens == 25
                && input.ContentChunkCount == 8
                && input.ReasoningChunkCount == 3
                && input.FailureCategory == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_FailedRun_WritesExactlyOneRunEnvelopeWithFailureCategory()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        var pump = CreatePump(store);

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

        await store.Received(1).AddRunEnvelopeAsync(
            Arg.Is<AgentRunEnvelopeInput>(input =>
                input.TerminalStatus == "failed"
                && !input.Success
                && input.FailureCategory == "ProviderUnreachable"
                && input.DurationMs == 42L
                && input.PromptTokens == null
                && input.CompletionTokens == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_WhenNoRunnerDuration_FallsBackToStartToCompleteElapsed()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        var pump = CreatePump(store);

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

        await store.Received(1).AddRunEnvelopeAsync(
            Arg.Is<AgentRunEnvelopeInput>(input => input.DurationMs == 750L),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeInterruptedAsync_WritesExactlyOneRunEnvelope()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        var pump = CreatePump(store);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var cursor = new NodeChatPumpCursor("partial", string.Empty);

        _ = await pump.TerminalizeInterruptedAsync(correlation, cursor, wasCancelled: false);

        await store.Received(1).AddRunEnvelopeAsync(
            Arg.Is<AgentRunEnvelopeInput>(input =>
                input.TerminalStatus == "interrupted"
                && !input.Success
                && input.InvocationId == null
                && input.RequestId == correlation.RequestId
                && input.DurationMs == 0L),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeInterruptedAsync_WhenCancelled_WritesCancelledEnvelope()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        var pump = CreatePump(store);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        _ = await pump.TerminalizeInterruptedAsync(correlation, new NodeChatPumpCursor(string.Empty, string.Empty), wasCancelled: true);

        await store.Received(1).AddRunEnvelopeAsync(
            Arg.Is<AgentRunEnvelopeInput>(input => input.TerminalStatus == "cancelled" && !input.Success),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TerminalizeAsync_WhenLedgerWriteThrows_DoesNotFailTheInvocation()
    {
        var store = Substitute.For<IAgentExecutionLogStore>();
        store.When(s => s.AddRunEnvelopeAsync(Arg.Any<AgentRunEnvelopeInput>(), Arg.Any<CancellationToken>()))
             .Do(_ => throw new InvalidOperationException("ledger unavailable"));
        var pump = CreatePump(store);

        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = correlation.ConversationId,
            Status = InvocationStatus.Completed,
            ModelUsed = "llama-3.1"
        };

        // The terminalization must complete normally even though the best-effort ledger write threw.
        var result = await pump.TerminalizeAsync(correlation, state, requestedModel: null);

        AssertEx.Equal("completed", result.TerminalStatus);
    }

    [Test]
    public async Task TerminalizeInterruptedAsync_WhenPersistedStatusWins_EnvelopeAndResultReflectPersisted()
    {
        // Simulate the transition guard rejecting an Interrupted write against an already-Cancelled row: the persistence
        // seam returns the Cancelled winning row. The envelope, the returned status, and the event type must all reflect
        // that persisted state rather than the requested Interrupted, so the ledger can never disagree with the row.
        var store = Substitute.For<IAgentExecutionLogStore>();
        var persistence = Substitute.For<INodeChatPersistenceService>();
        var correlation = new NodeChatMessageCorrelation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
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
        var provider = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var pump = new NodeChatInvocationPump(persistence, TimeProvider.System, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<NodeChatInvocationPump>.Instance);

        var result = await pump.TerminalizeInterruptedAsync(correlation, new NodeChatPumpCursor("partial", string.Empty), wasCancelled: false);

        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, result.TerminalStatus);
        AssertEx.Equal(ChatStreamEventTypes.AssistantCancelled, result.EventType);
        await store.Received(1).AddRunEnvelopeAsync(
            Arg.Is<AgentRunEnvelopeInput>(input => input.TerminalStatus == NodeChatMessageStatusValues.Cancelled && !input.Success),
            Arg.Any<CancellationToken>());
    }

    private static NodeChatInvocationPump CreatePump(IAgentExecutionLogStore store)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        // The pump now derives the envelope + result from the PERSISTED winning row, so the terminalize seam must return
        // a message. Echo the requested terminal (the happy-path winning status) so the envelope reflects a successful
        // terminalize; the transition-table rejection path is covered directly in NodeChatPersistenceServiceTests.
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
        var provider = new ServiceCollection()
                       .AddSingleton(store)
                       .BuildServiceProvider();

        return new NodeChatInvocationPump(persistence,
            TimeProvider.System,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatInvocationPump>.Instance);
    }
}
