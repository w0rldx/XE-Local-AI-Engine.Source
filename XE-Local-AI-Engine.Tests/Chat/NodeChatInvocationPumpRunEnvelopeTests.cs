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

    private static NodeChatInvocationPump CreatePump(IAgentExecutionLogStore store)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        var provider = new ServiceCollection()
                       .AddSingleton(store)
                       .BuildServiceProvider();

        return new NodeChatInvocationPump(persistence,
            TimeProvider.System,
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatInvocationPump>.Instance);
    }
}
