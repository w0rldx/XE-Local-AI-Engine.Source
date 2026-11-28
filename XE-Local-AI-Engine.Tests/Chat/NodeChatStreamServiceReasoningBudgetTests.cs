namespace XE_Local_AI_Engine.Tests.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static NodeChatStreamServiceTestSupport;

/// <summary>
///     A hub send carries raw application sampling options that bypass the conversation PATCH validator, so the send path
///     bounds the thinking budget itself, before anything is persisted.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceReasoningBudgetTests
{
    [Test]
    [Arguments(127)]
    [Arguments(131_073)]
    public async Task SendMessageAsync_WithABudgetOutsideTheSharedRange_IsRefusedBeforeAnythingIsPersisted(int budgetTokens)
    {
        var harness = new Harness();

        var exception = AssertEx.Throws<NodeChatInvalidRequestException>(() => _ = harness.Service.SendMessageAsync(harness.Request(budgetTokens)));

        AssertEx.Equal("Reasoning budget must be from 128 to 131072.", exception.Message);
        await harness.Persistence.DidNotReceiveWithAnyArgs().PersistUserMessageAsync(default!, default);
        AssertEx.False(harness.Runner.CaptureObserved, "a refused send never runs");
    }

    [Test]
    [Arguments(128)]
    [Arguments(131_072)]
    public async Task SendMessageAsync_WithABudgetOnTheSharedBound_Runs(int budgetTokens)
    {
        var harness = new Harness();

        await foreach (var _ in harness.Service.SendMessageAsync(harness.Request(budgetTokens)))
        {
            // Drained for the run.
        }

        AssertEx.True(harness.Runner.CaptureObserved);
    }

    private sealed class Harness
    {
        private readonly Guid _conversationId = Guid.NewGuid();
        private readonly Guid _assistantMessageId = Guid.NewGuid();
        private readonly Guid _requestId = Guid.NewGuid();

        public Harness()
        {
            Persistence = CreatePersistence(_conversationId, _assistantMessageId, _requestId, _ => { });
            var dispatcher = new RecordingWorkerEventDispatcher();
            Runner = new ContextCapturingInvocationRunner(dispatcher);
            Service = CreateServiceWithScopeFactory(Persistence, Runner, dispatcher, CreateKnowledgeScopeFactory());
        }

        public INodeChatPersistenceService Persistence { get; }

        public ContextCapturingInvocationRunner Runner { get; }

        public NodeChatStreamService Service { get; }

        public NodeChatStreamRequest Request(int budgetTokens) =>
            new(_conversationId,
                "hello",
                MessageId: _assistantMessageId,
                RequestId: _requestId,
                SamplingOptions: new SamplingOptions
                {
                    ReasoningBudgetTokens = budgetTokens
                });
    }
}
