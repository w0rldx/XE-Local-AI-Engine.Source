namespace XE_Local_AI_Engine.Tests.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

public sealed class NodeChatRemotePersistenceCoordinatorTests
{
    [Test]
    public async Task BeginAsync_EnsuresConversationAndPersistsTurnsWithRemoteOrigin()
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        StubPersistence(persistence);
        var pump = Substitute.For<INodeChatInvocationPump>();
        var coordinator = new NodeChatRemotePersistenceCoordinator(persistence, pump, TimeProvider.System);

        // WithUserMessage clears the builder's seed message, so the context is exactly these three (ordered).
        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("first question")
                                           .WithConversationMessage(MessageRole.Assistant, "first answer", 1)
                                           .WithConversationMessage(MessageRole.User, "latest question", 2)
                                           .Build();

        await coordinator.BeginAsync(package);

        // Conversation ensured with Origin=Remote and title from the FIRST user entry.
        await persistence.Received(1).EnsureConversationAsync(Arg.Is<NodeChatEnsureConversationRequest>(request =>
                request.ConversationId == package.ConversationId
                && request.Origin == NodeChatOriginValues.Remote
                && request.Title == "first question"),
            Arg.Any<CancellationToken>());

        // User turn synthesized from the LAST user entry, persisted Origin=Remote.
        await persistence.Received(1).PersistUserMessageAsync(Arg.Is<NodeChatPersistUserMessageRequest>(request =>
                request.ConversationId == package.ConversationId
                && request.Content == "latest question"
                && request.Origin == NodeChatOriginValues.Remote),
            Arg.Any<CancellationToken>());

        // Assistant placeholder uses InvocationId as RequestId, Origin=Remote, a freshly minted message id.
        await persistence.Received(1).CreateAssistantPlaceholderAsync(Arg.Is<NodeChatCreateAssistantPlaceholderRequest>(request =>
                request.ConversationId == package.ConversationId
                && request.RequestId == package.InvocationId
                && request.MessageId != Guid.Empty
                && request.MessageId != package.InvocationId
                && request.Origin == NodeChatOriginValues.Remote),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Session_ApplyTerminalState_TerminalizesThroughPump()
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        StubPersistence(persistence);
        var pump = Substitute.For<INodeChatInvocationPump>();
        pump.FlushDeltaAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<InvocationState>(), Arg.Any<NodeChatPumpCursor>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new NodeChatPumpFlushResult(callInfo.ArgAt<NodeChatPumpCursor>(2), null, null, null));
        var coordinator = new NodeChatRemotePersistenceCoordinator(persistence, pump, TimeProvider.System);
        var package = RuntimePackageBuilder.Valid().WithUserMessage("q").Build();

        var session = await coordinator.BeginAsync(package);

        var terminal = new InvocationState
        {
            InvocationId = package.InvocationId,
            ConversationId = package.ConversationId,
            Status = InvocationStatus.Completed,
            StreamedContent = "answer",
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };

        var done = await session.ApplyAsync(terminal);

        AssertEx.True(done, "Applying a terminal state should report persistence complete.");
        await pump.Received(1).TerminalizeAsync(Arg.Any<NodeChatMessageCorrelation>(), terminal, package.ModelProfile);
    }

    [Test]
    public async Task Session_TerminalizeInterrupted_WhenNoTerminalApplied_PersistsInterrupted()
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        StubPersistence(persistence);
        var pump = Substitute.For<INodeChatInvocationPump>();
        var coordinator = new NodeChatRemotePersistenceCoordinator(persistence, pump, TimeProvider.System);
        var package = RuntimePackageBuilder.Valid().WithUserMessage("q").Build();

        var session = await coordinator.BeginAsync(package);
        await session.TerminalizeInterruptedAsync(wasCancelled: false);

        await pump.Received(1).TerminalizeInterruptedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<NodeChatPumpCursor>(), false);
    }

    private static void StubPersistence(INodeChatPersistenceService persistence)
    {
        var conversation = new NodeChatConversationDto(Guid.NewGuid(), "t", null, 1, 1, false, []);
        var message = new NodeChatPersistedMessageDto(Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            1,
            "assistant",
            string.Empty,
            null,
            NodeChatMessageStatusValues.Pending,
            1,
            1,
            null,
            null,
            null);

        persistence.EnsureConversationAsync(Arg.Any<NodeChatEnsureConversationRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>()).Returns(message);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>()).Returns(message);
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(message);
    }
}
