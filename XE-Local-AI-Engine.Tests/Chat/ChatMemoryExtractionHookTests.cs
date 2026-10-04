namespace XE_Local_AI_Engine.Tests.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     A failed turn feeds memory extraction only when the agent itself failed: a timeout, an open breaker or an
///     absent model teaches nothing and only queues another job behind a stalled slot.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ChatMemoryExtractionHookTests
{
    [Test]
    [Arguments(FailureCategory.Timeout)]
    [Arguments(FailureCategory.ProviderUnreachable)]
    [Arguments(FailureCategory.ModelUnavailable)]
    [Arguments(FailureCategory.ModelLoadFailed)]
    [Arguments(FailureCategory.ContextWindowExceeded)]
    [Arguments(FailureCategory.Unexpected)]
    public void Build_FailedOnInfrastructure_DoesNotDispatch(FailureCategory category)
    {
        var dispatcher = Substitute.For<IMemoryExtractionDispatcher>();

        Fire(dispatcher, InvocationStatus.Failed, category);

        dispatcher.DidNotReceive().Dispatch(Arg.Any<MemoryExtractionDispatchContext>(), Arg.Any<MemoryExtractionRunInput>());
    }

    [Test]
    [Arguments(FailureCategory.AgentToolCall)]
    [Arguments(FailureCategory.AgentRuntime)]
    public void Build_FailedByTheAgent_DispatchesAFailedRun(FailureCategory category)
    {
        var dispatcher = Substitute.For<IMemoryExtractionDispatcher>();

        Fire(dispatcher, InvocationStatus.Failed, category);

        dispatcher.Received(1).Dispatch(Arg.Any<MemoryExtractionDispatchContext>(), Arg.Is<MemoryExtractionRunInput>(run => run.Failed));
    }

    [Test]
    public void Build_Completed_Dispatches()
    {
        var dispatcher = Substitute.For<IMemoryExtractionDispatcher>();

        Fire(dispatcher, InvocationStatus.Completed, category: null);

        dispatcher.Received(1).Dispatch(Arg.Any<MemoryExtractionDispatchContext>(), Arg.Is<MemoryExtractionRunInput>(run => !run.Failed));
    }

    private static void Fire(IMemoryExtractionDispatcher dispatcher, InvocationStatus status, FailureCategory? category)
    {
        var conversationId = Guid.NewGuid();
        var hook = ChatMemoryExtractionHook.Build(dispatcher,
            new ResolvedAgentRuntime("prompt", [], "model-x", null, 1, Guid.NewGuid()),
            conversationId,
            memoryExcluded: false,
            RuntimePackageBuilder.Valid().Build(),
            "model-x",
            static () => []);

        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            Status = status,
            FailureCategory = category,
            StreamedContent = "answer"
        };

        hook(state, new NodeChatPumpTerminalResult
        {
            Persisted = new NodeChatPersistedMessageDto
            {
                MessageId = Guid.NewGuid(),
                ConversationId = conversationId,
                RequestId = state.InvocationId,
                Sequence = 1,
                Role = "assistant",
                Content = "answer",
                Reasoning = null,
                Status = "completed",
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                Model = "model-x",
                Error = null,
                MetadataJson = null,
                InputCount = null,
                OutputCount = null
            },
            TerminalStatus = "completed",
            EventType = "assistant-completed"
        });
    }
}
