namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InvocationResumeRegistryTests
{
    [Test]
    public void TryGetLiveInvocation_WhenAssigned_ReturnsSnapshot()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Assigned));

        var live = AssertEx.NotNull(registry.TryGetLiveInvocation(invocationId));
        AssertEx.Equal(invocationId, live.InvocationId);
        AssertEx.Equal(conversationId, live.ConversationId);
        AssertEx.Equal(InvocationStatus.Assigned, live.Status);
    }

    [Test]
    public void TryGetLiveInvocation_PreservesGenerationDurationMsThroughClone()
    {
        // Regression: InvocationResumeRegistry.Clone() copied the token fields but dropped GenerationDurationMs,
        // so the resume snapshot lost the duration even though the published state carried it. The value crosses two
        // clones here (Publish stores Clone(state); TryGetLiveInvocation returns Clone(LatestState)) — both must keep it.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, Guid.NewGuid(), InvocationStatus.Running, "hi", 1234));

        var live = AssertEx.NotNull(registry.TryGetLiveInvocation(invocationId));
        AssertEx.Equal(1234L, live.GenerationDurationMs);
    }

    [Test]
    public void TryGetLiveInvocation_WhenUnknown_ReturnsNull()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);

        AssertEx.Null(registry.TryGetLiveInvocation(Guid.NewGuid()));
    }

    [Test]
    public void TryGetLiveInvocation_WhenTerminal_ReturnsNullAndRemovesEntry()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, Guid.NewGuid(), InvocationStatus.Running, "hi"));
        RaiseState(dispatcher, NewState(invocationId, Guid.NewGuid(), InvocationStatus.Completed, "hi there"));

        AssertEx.Null(registry.TryGetLiveInvocation(invocationId));
    }

    [Test]
    public async Task ResumeAsync_ReplaysAccumulatedSnapshotThenLiveDeltasThenTerminal()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        // Invocation is already mid-stream when the client reconnects.
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "Hello"));

        var events = new List<ChatStreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                events.Add(streamEvent);
            }
        });

        // Wait for the snapshot replay before pushing more deltas so ordering is deterministic.
        await AssertEx.EventuallyAsync(() => events.Count >= 1, TimeSpan.FromSeconds(5));

        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "Hello world"));
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "Hello world"));

        await consumer;

        // Snapshot delta replays the accumulated content.
        var snapshot = events[0];
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, snapshot.Type);
        AssertEx.Equal("Hello", snapshot.Delta);
        AssertEx.Equal(conversationId, snapshot.ConversationId);
        AssertEx.Equal(invocationId, snapshot.RequestId);

        // Live delta carries only the newly appended fragment.
        var delta = events[1];
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, delta.Type);
        AssertEx.Equal(" world", delta.Delta);

        // Terminal event closes the stream.
        var terminal = events[^1];
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, terminal.Type);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, terminal.Status);
    }

    [Test]
    public async Task ResumeAsync_WhenUnknownInvocation_Throws()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);

        await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in registry.ResumeAsync(Guid.NewGuid(), CancellationToken.None))
            {
                // No events expected — the enumerator throws on first move.
            }
        });
    }

    [Test]
    public async Task ResumeAsync_WhenAlreadyTerminal_Throws()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, Guid.NewGuid(), InvocationStatus.Running, "done"));
        RaiseState(dispatcher, NewState(invocationId, Guid.NewGuid(), InvocationStatus.Completed, "done"));

        await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                // No events expected — the enumerator throws on first move because the invocation is terminal.
            }
        });
    }

    private static InvocationResumeRegistry CreateRegistry(IWorkerEventDispatcher dispatcher)
    {
        return new InvocationResumeRegistry(dispatcher,
            TimeProvider.System,
            NullLogger<InvocationResumeRegistry>.Instance);
    }

    private static InvocationState NewState(Guid invocationId,
        Guid conversationId,
        InvocationStatus status,
        string content = "",
        long? generationDurationMs = null)
    {
        return new InvocationState
        {
            InvocationId = invocationId,
            ConversationId = conversationId,
            Status = status,
            StreamedContent = content,
            GenerationDurationMs = generationDurationMs,
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static void RaiseState(IWorkerEventDispatcher dispatcher, InvocationState state)
    {
        dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher, new InvocationStateChangedEventArgs(state));
    }
}
