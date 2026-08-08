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
                                           .WithConversationMessage(MessageRole.Assistant, "first answer", sortOrder: 1)
                                           .WithConversationMessage(MessageRole.User, "latest question", sortOrder: 2)
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
            .Returns(callInfo => new NodeChatPumpFlushResult(callInfo.ArgAt<NodeChatPumpCursor>(2), Persisted: null, ContentDelta: null, ReasoningDelta: null));
        var coordinator = new NodeChatRemotePersistenceCoordinator(persistence, pump, TimeProvider.System);
        var package = RuntimePackageBuilder.Valid().WithUserMessage("q").Build();

        var session = AssertEx.NotNull(await coordinator.BeginAsync(package));

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

        var session = AssertEx.NotNull(await coordinator.BeginAsync(package));
        await session.TerminalizeInterruptedAsync(false);

        await pump.Received(1).TerminalizeInterruptedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<NodeChatPumpCursor>(), wasCancelled: false);
    }

    [Test]
    public async Task BeginAsync_WhenStreamingMarkRejected_ReturnsNullWithoutOpeningSession()
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        StubPersistence(persistence);
        // The assistant placeholder was terminalized (e.g. an early cancel) before the coordinator could mark it streaming,
        // so the guarded streaming mark is a no-op and reports the true terminal status. BeginAsync must abort honestly.
        var cancelledRow = new NodeChatPersistedMessageDto(Guid.NewGuid(),
            Guid.NewGuid(),
            RequestId: null,
            Sequence: 1,
            "assistant",
            string.Empty,
            Reasoning: null,
            NodeChatMessageStatusValues.Cancelled,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Model: null,
            Error: null,
            MetadataJson: null);
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(cancelledRow);
        var pump = Substitute.For<INodeChatInvocationPump>();
        var coordinator = new NodeChatRemotePersistenceCoordinator(persistence, pump, TimeProvider.System);
        var package = RuntimePackageBuilder.Valid().WithUserMessage("q").Build();

        var session = await coordinator.BeginAsync(package);

        AssertEx.Null(session, "A rejected streaming mark must abort BeginAsync with a null session (no persistence against a terminal row).");
    }

    private static void StubPersistence(INodeChatPersistenceService persistence)
    {
        var conversation = new NodeChatConversationDto(Guid.NewGuid(), "t", UserId: null, CreatedAtUtc: 1, LastSeenUtc: 1, Purged: false, []);
        var message = new NodeChatPersistedMessageDto(Guid.NewGuid(),
            Guid.NewGuid(),
            RequestId: null,
            Sequence: 1,
            "assistant",
            string.Empty,
            Reasoning: null,
            NodeChatMessageStatusValues.Pending,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Model: null,
            Error: null,
            MetadataJson: null);

        persistence.EnsureConversationAsync(Arg.Any<NodeChatEnsureConversationRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>()).Returns(message);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>()).Returns(message);
        // The streaming mark must report the row landed in Streaming, otherwise BeginAsync aborts (returns null): the
        // coordinator only opens a session against a row it successfully marked streaming.
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(message with
                   {
                       Status = NodeChatMessageStatusValues.Streaming
                   });
    }
}
