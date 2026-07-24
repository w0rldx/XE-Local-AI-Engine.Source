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

        RaiseState(dispatcher, NewState(invocationId, Guid.NewGuid(), InvocationStatus.Running, "hi", generationDurationMs: 1234));

        var live = AssertEx.NotNull(registry.TryGetLiveInvocation(invocationId));
        AssertEx.Equal(expected: 1234L, live.GenerationDurationMs);
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

        // The replay is a pure SNAPSHOT event: full Content, NO delta fields. The reconnecting client applies
        // Content as a replacement; a delta here would be appended to whatever it already rendered before the
        // reconnect, duplicating it.
        var snapshot = events[0];
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, snapshot.Type);
        AssertEx.Null(snapshot.Delta);
        AssertEx.Null(snapshot.ReasoningDelta);
        AssertEx.Equal("Hello", snapshot.Content);
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

        // Resume streams number from zero, contiguous and ascending — the client rebases them onto the original
        // stream's sequence space at the reconnect boundary.
        AssertEx.Equal(expected: 0L, events[0].Sequence);
        for (var index = 1; index < events.Count; index++)
        {
            AssertEx.Equal(events[index - 1].Sequence + 1, events[index].Sequence);
        }
    }

    [Test]
    public async Task ResumeAsync_ReplaysToolTimelineBeforeSnapshotThenForwardsLiveToolEvents()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        // Mid-stream with one completed tool call before the client reconnects.
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "Hello"));
        RaiseToolCall(dispatcher, NewToolCall(invocationId, "call-1", "calculate", ToolCallLifecyclePhase.Requested, arguments: "{\"a\":1}"));
        RaiseToolCall(dispatcher, NewToolCall(invocationId, "call-1", "calculate", ToolCallLifecyclePhase.Completed, result: "2"));

        var events = new List<ChatStreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                events.Add(streamEvent);
            }
        });

        // Tool timeline replays first, then the content snapshot.
        await AssertEx.EventuallyAsync(() => events.Count >= 3, TimeSpan.FromSeconds(5));

        var requested = events[0];
        AssertEx.Equal(ChatStreamEventTypes.ToolCallRequested, requested.Type);
        AssertEx.Equal("call-1", requested.ToolCallId);
        AssertEx.Equal("calculate", requested.ToolName);
        AssertEx.Equal("{\"a\":1}", requested.Arguments);
        AssertEx.Equal(conversationId, requested.ConversationId);

        var completed = events[1];
        AssertEx.Equal(ChatStreamEventTypes.ToolCallCompleted, completed.Type);
        AssertEx.Equal("call-1", completed.ToolCallId);
        AssertEx.Equal("2", completed.Result);

        var snapshot = events[2];
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, snapshot.Type);
        AssertEx.Equal("Hello", snapshot.Content);
        AssertEx.Null(snapshot.Delta);

        // A tool call fired while the resume consumer is attached streams through live, then the terminal closes.
        RaiseToolCall(dispatcher, NewToolCall(invocationId, "call-2", "search_knowledge_base", ToolCallLifecyclePhase.Requested));
        await AssertEx.EventuallyAsync(() => events.Count >= 4, TimeSpan.FromSeconds(5));
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "Hello"));
        await consumer;

        var liveTool = events[3];
        AssertEx.Equal(ChatStreamEventTypes.ToolCallRequested, liveTool.Type);
        AssertEx.Equal("call-2", liveTool.ToolCallId);

        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, events[^1].Type);

        // Contiguous ascending from zero across tool replays, snapshot, live tool events, and terminal.
        AssertEx.Equal(expected: 0L, events[0].Sequence);
        for (var index = 1; index < events.Count; index++)
        {
            AssertEx.Equal(events[index - 1].Sequence + 1, events[index].Sequence);
        }
    }

    [Test]
    public async Task ResumeAsync_ReplaysReasoningAsSnapshotWithoutDelta()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "Hi", thinking: "Let me think"));

        var events = new List<ChatStreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                events.Add(streamEvent);
            }
        });

        await AssertEx.EventuallyAsync(() => events.Count >= 1, TimeSpan.FromSeconds(5));
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "Hi", thinking: "Let me think"));
        await consumer;

        var snapshot = events[0];
        AssertEx.Null(snapshot.Delta);
        AssertEx.Null(snapshot.ReasoningDelta);
        AssertEx.Equal("Hi", snapshot.Content);
        AssertEx.Equal("Let me think", snapshot.Reasoning);
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

    [Test]
    public async Task ResumeAsync_WhenTerminalRacesBeforeFirstEnumeration_YieldsTerminalAndCompletes()
    {
        // The invocation can reach its terminal in the window between ResumeAsync's non-terminal validation
        // and the lazy Subscribe at the first enumeration. When it does, OnInvocationStateChanged runs TryRemove +
        // Publish(terminal) + Complete() before any subscriber channel is registered, so the freshly-registered channel
        // would never be completed and the consumer's ReadAllAsync would block forever. The consumer must instead emit
        // the terminal from the snapshot and finish. No existing test exercises this ordering — they all subscribe
        // (first MoveNextAsync) before the terminal is raised.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "Hello"));

        // Obtain the enumerator WITHOUT advancing it: ResumeAsync validates synchronously (the invocation is still
        // non-terminal here), but Subscribe only runs at the first MoveNextAsync below.
        var enumerator = registry.ResumeAsync(invocationId, CancellationToken.None).GetAsyncEnumerator(CancellationToken.None);
        try
        {
            // Terminal races in before the first enumeration.
            RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "Hello world"));

            var events = new List<ChatStreamEvent>();
            var drain = Task.Run(async () =>
            {
                while (await enumerator.MoveNextAsync())
                {
                    events.Add(enumerator.Current);
                }
            });

            var finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5)));
            AssertEx.True(finished == drain, "Resume enumeration must complete instead of blocking on a channel that never carries the terminal.");
            await drain;

            AssertEx.True(events.Count > 0, "Expected at least the snapshot replay and the terminal.");
            var terminal = events[^1];
            AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, terminal.Type);
            AssertEx.Equal(NodeChatMessageStatusValues.Completed, terminal.Status);
            AssertEx.Equal("Hello world", terminal.Content);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
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
        long? generationDurationMs = null,
        string thinking = "")
    {
        return new InvocationState
        {
            InvocationId = invocationId,
            ConversationId = conversationId,
            Status = status,
            StreamedContent = content,
            StreamedThinkingContent = thinking,
            GenerationDurationMs = generationDurationMs,
            StartedAt = DateTimeOffset.UtcNow,
            LastUpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ToolCallLifecyclePayload NewToolCall(Guid invocationId,
        string toolCallId,
        string toolName,
        ToolCallLifecyclePhase phase,
        string? arguments = null,
        string? result = null)
    {
        return new ToolCallLifecyclePayload
        {
            InvocationId = invocationId,
            ToolCallId = toolCallId,
            ToolName = toolName,
            Phase = phase,
            Arguments = arguments,
            Result = result
        };
    }

    private static void RaiseState(IWorkerEventDispatcher dispatcher, InvocationState state)
    {
        dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher, new InvocationStateChangedEventArgs(state));
    }

    private static void RaiseToolCall(IWorkerEventDispatcher dispatcher, ToolCallLifecyclePayload payload)
    {
        dispatcher.ToolCallLifecycleChanged += Raise.EventWith(dispatcher, new ToolCallLifecycleChangedEventArgs(payload));
    }
}
