namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
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

        // The replay is an assistant-snapshot: full Content, NO delta fields. The reconnecting client applies Content
        // as a replacement; a delta here would be appended to whatever it already rendered before the reconnect,
        // duplicating it. Its offsets are what re-seat the client's delta-offset counters after the gap.
        var snapshot = events[0];
        AssertEx.Equal(ChatStreamEventTypes.AssistantSnapshot, snapshot.Type);
        AssertEx.Null(snapshot.Delta);
        AssertEx.Null(snapshot.ReasoningDelta);
        AssertEx.Equal("Hello", snapshot.Content);
        AssertEx.Equal(expected: 5L, snapshot.ContentOffset);
        AssertEx.Equal(conversationId, snapshot.ConversationId);
        AssertEx.Equal(invocationId, snapshot.RequestId);
        // A snapshot is a mid-stream replacement, never a terminal: the turn continues after it.
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, snapshot.Status);

        // Live delta carries only the newly appended fragment, at the offset the snapshot ended on — so the client can
        // append it and detect a gap from the offsets alone.
        var delta = events[1];
        AssertEx.Equal(ChatStreamEventTypes.AssistantDelta, delta.Type);
        AssertEx.Equal(" world", delta.Delta);
        AssertEx.Equal(expected: 5L, delta.ContentOffset);
        AssertEx.Equal(snapshot.ContentOffset, delta.ContentOffset);
        // The delta-only protocol: a live frame never re-sends the accumulated text.
        AssertEx.Null(delta.Content);
        AssertEx.Null(delta.Reasoning);

        // Terminal event closes the stream, and still carries the full text — one frame per turn, and the backstop
        // that converges a client whose delta stream fell behind.
        var terminal = events[^1];
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, terminal.Type);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, terminal.Status);
        AssertEx.Equal("Hello world", terminal.Content);

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
        AssertEx.Equal(ChatStreamEventTypes.AssistantSnapshot, snapshot.Type);
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
        AssertEx.Equal(ChatStreamEventTypes.AssistantSnapshot, snapshot.Type);
        AssertEx.Null(snapshot.Delta);
        AssertEx.Null(snapshot.ReasoningDelta);
        AssertEx.Equal("Hi", snapshot.Content);
        AssertEx.Equal("Let me think", snapshot.Reasoning);
        // Reasoning gets its own offset, so a resumed turn stays diffable on both sides independently.
        AssertEx.Equal(expected: 2L, snapshot.ContentOffset);
        AssertEx.Equal(expected: 12L, snapshot.ReasoningOffset);
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

            await AssertEx.CompletesAsync(drain,
                TestBudgets.Contended,
                "Resume enumeration must complete instead of blocking on a channel that never carries the terminal.");

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

    [Test]
    public async Task ResumeAsync_WhenPendingQuestion_ReplaysQuestionRequestedOnceWithTheQuestions()
    {
        // A mid-turn reload used to lose the prompt permanently — question-requested is emitted from one live
        // subscription and is never accumulated into parts[], so the pending slot on InvocationState is the only
        // surface a reconnecting browser has. The turn cannot proceed without an answer, so losing it wastes the turn.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var parked = NewState(invocationId, conversationId, InvocationStatus.Running, "thinking");
        parked.PendingQuestion = NewQuestion("question-1", "call-1");
        RaiseState(dispatcher, parked);

        var events = new List<ChatStreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                events.Add(streamEvent);
            }
        });

        await AssertEx.EventuallyAsync(() => events.Any(evt => evt.Type == ChatStreamEventTypes.QuestionRequested), TimeSpan.FromSeconds(5));

        // More publishes while the question is still pending must NOT re-emit it: every state publish carries the
        // pending slot, so without the dedupe the card would be re-pushed on every delta.
        var stillParked = NewState(invocationId, conversationId, InvocationStatus.Running, "thinking more");
        stillParked.PendingQuestion = NewQuestion("question-1", "call-1");
        RaiseState(dispatcher, stillParked);
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "done"));

        await consumer;

        var replayed = events.Where(evt => evt.Type == ChatStreamEventTypes.QuestionRequested).ToList();
        AssertEx.Equal(expected: 1, replayed.Count);
        AssertEx.Equal("question-1", replayed[0].QuestionRequestId);
        AssertEx.Equal("call-1", replayed[0].ToolCallId);
        AssertEx.Equal("ask_user", replayed[0].ToolName);
        // The questions themselves must ride the replay: a client cannot render an answerable prompt from an id.
        AssertEx.Contains(replayed[0].Questions, "Which auth method?");
        // Sequence numbers stay contiguous and ascending across the replay (the client rebases them at the boundary).
        AssertEx.True(events.Select(evt => evt.Sequence).SequenceEqual(Enumerable.Range(start: 0, events.Count).Select(static index => (long)index)),
            "Replayed and live events must share one contiguous ascending sequence space.");
    }

    [Test]
    public async Task ResumeAsync_WhenPendingApproval_ReplaysApprovalRequested()
    {
        // Same defect, pre-existing on the shipped tool-approval feature: a reload lost the Approve/Deny controls and
        // the turn stayed blocked until it timed out. The same replay now closes that gap.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var parked = NewState(invocationId, conversationId, InvocationStatus.Running, "thinking");
        parked.PendingApproval = new InvocationApprovalState("approval-1", "Run a command", DateTimeOffset.UtcNow)
        {
            CallId = "call-7",
            ToolName = "run_command",
            SessionScopeEligible = true
        };
        RaiseState(dispatcher, parked);

        var events = new List<ChatStreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                events.Add(streamEvent);
            }
        });

        await AssertEx.EventuallyAsync(() => events.Any(evt => evt.Type == ChatStreamEventTypes.ApprovalRequested), TimeSpan.FromSeconds(5));
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "done"));
        await consumer;

        var replayed = events.Single(evt => evt.Type == ChatStreamEventTypes.ApprovalRequested);
        AssertEx.Equal("approval-1", replayed.ApprovalRequestId);
        AssertEx.Equal("call-7", replayed.ToolCallId);
        AssertEx.Equal("run_command", replayed.ToolName);
        // The runner's session-scope answer survives the reload, so the re-rendered card offers the same controls.
        AssertEx.Equal(expected: true, replayed.SessionScopeEligible);
    }

    [Test]
    public async Task ResumeAsync_WhenApprovalHasNoCallId_StillReplaysWithNullToolCallId()
    {
        // A platform-hub approval carries only an id and a description. Degrade gracefully: the prompt still reaches
        // the client, it just cannot be attached to a specific tool-call card.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        var parked = NewState(invocationId, conversationId, InvocationStatus.Running, "thinking");
        parked.PendingApproval = new InvocationApprovalState("approval-2", "Run a command", DateTimeOffset.UtcNow);
        RaiseState(dispatcher, parked);

        var events = new List<ChatStreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                events.Add(streamEvent);
            }
        });

        await AssertEx.EventuallyAsync(() => events.Any(evt => evt.Type == ChatStreamEventTypes.ApprovalRequested), TimeSpan.FromSeconds(5));
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "done"));
        await consumer;

        var replayed = events.Single(evt => evt.Type == ChatStreamEventTypes.ApprovalRequested);
        AssertEx.Equal("approval-2", replayed.ApprovalRequestId);
        AssertEx.Null(replayed.ToolCallId);
        AssertEx.Null(replayed.ToolName);
        // Nothing recorded the runner's answer, so the replay fails CLOSED rather than letting the client fall back to
        // the tool catalog and offer a session scope the node may never honor.
        AssertEx.Equal(expected: false, replayed.SessionScopeEligible);
    }

    [Test]
    public async Task ResumeAsync_WhenQuestionRaisedWhileAttached_EmitsItOnceFromTheLiveState()
    {
        // The prompt can also arrive AFTER the reconnect. The dispatcher records the pending slot before fanning the
        // live event out, so the state publish carries it — and the same dedupe keeps it to exactly one emit.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher);
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "thinking"));

        var events = new List<ChatStreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                events.Add(streamEvent);
            }
        });

        await AssertEx.EventuallyAsync(() => events.Count >= 1, TimeSpan.FromSeconds(5));
        AssertEx.False(events.Any(evt => evt.Type == ChatStreamEventTypes.QuestionRequested), "No question was pending at reconnect time.");

        var parked = NewState(invocationId, conversationId, InvocationStatus.Running, "thinking");
        parked.PendingQuestion = NewQuestion("question-live", "call-live");
        RaiseState(dispatcher, parked);

        await AssertEx.EventuallyAsync(() => events.Any(evt => evt.Type == ChatStreamEventTypes.QuestionRequested), TimeSpan.FromSeconds(5));
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "done"));
        await consumer;

        AssertEx.Equal(expected: 1, events.Count(evt => evt.Type == ChatStreamEventTypes.QuestionRequested));
        AssertEx.Equal("question-live", events.Single(evt => evt.Type == ChatStreamEventTypes.QuestionRequested).QuestionRequestId);
    }

    private static InvocationUserQuestionState NewQuestion(string requestId, string callId)
    {
        return new InvocationUserQuestionState(requestId,
            callId,
            "ask_user",
            [
                new UserQuestionSpec("Auth",
                    "Which auth method?",
                    MultiSelect: false,
                    [
                        new UserQuestionOption("OAuth", Description: null, Recommended: true),
                        new UserQuestionOption("API key", Description: null, Recommended: false)
                    ])
            ],
            DateTimeOffset.UtcNow);
    }

    [Test]
    public async Task ResumeAsync_PastTheSubscriberCap_RejectsWhileTheExistingSubscribersKeepStreaming()
    {
        // The cap REJECTS rather than evicting: a tab stuck in a reconnect loop must never knock a working browser off
        // its own stream. The rejected caller sees the same failure shape as "not resumable" and refetches instead.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher,
            new ChatStreamBudgetOptions
            {
                MaxSubscribersPerInvocation = 2
            });
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "Hello"));

        var firstEvents = new List<ChatStreamEvent>();
        var secondEvents = new List<ChatStreamEvent>();
        var first = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                firstEvents.Add(streamEvent);
            }
        });
        var second = Task.Run(async () =>
        {
            await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                secondEvents.Add(streamEvent);
            }
        });

        // Both are attached (each has replayed its opening snapshot) before the third one asks.
        await AssertEx.EventuallyAsync(() => firstEvents.Count >= 1 && secondEvents.Count >= 1, TimeSpan.FromSeconds(5));

        await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in registry.ResumeAsync(invocationId, CancellationToken.None))
            {
                // No events expected — the cap rejects this consumer at its first enumeration.
            }
        });

        // The rejection changed nothing for the two that were already there: both still receive the live delta and
        // the terminal.
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "Hello world"));
        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Completed, "Hello world"));

        await first;
        await second;

        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, firstEvents[^1].Type);
        AssertEx.Equal(ChatStreamEventTypes.AssistantCompleted, secondEvents[^1].Type);
        AssertEx.ContainsSingle(firstEvents, streamEvent => streamEvent.Delta == " world");
        AssertEx.ContainsSingle(secondEvents, streamEvent => streamEvent.Delta == " world");
    }

    [Test]
    public async Task ResumeAsync_WhenTheReplaySnapshotExceedsTheCap_ReconcilesInsteadOfReplaying()
    {
        // Above the cap the replay would be the single largest frame the protocol can produce. Reconciling instead
        // costs one refetch of the persisted conversation, which holds the same text — and avoids inventing a
        // truncated-snapshot semantic that every reader of a snapshot would then have to understand.
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var registry = CreateRegistry(dispatcher,
            new ChatStreamBudgetOptions
            {
                MaxReplaySnapshotChars = 8
            });
        var invocationId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();

        RaiseState(dispatcher, NewState(invocationId, conversationId, InvocationStatus.Running, "well past eight characters"));

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in registry.ResumeAsync(invocationId, CancellationToken.None))
        {
            events.Add(streamEvent);
        }

        // One reconcile, and the stream ENDS — it does not go on to replay the very snapshot the cap refused.
        AssertEx.Equal(expected: 1, events.Count);
        AssertEx.Equal(ChatStreamEventTypes.AssistantReconcile, events[0].Type);
        AssertEx.Equal(conversationId, events[0].ConversationId);
        AssertEx.Equal(invocationId, events[0].RequestId);
        AssertEx.Null(events[0].Content);

        // And the invocation is still live for the refetch/re-resume that follows: the cap rejects the REPLAY, not
        // the run.
        AssertEx.NotNull(registry.TryGetLiveInvocation(invocationId));
    }

    private static InvocationResumeRegistry CreateRegistry(IWorkerEventDispatcher dispatcher, ChatStreamBudgetOptions? budget = null)
    {
        return new InvocationResumeRegistry(dispatcher,
            TimeProvider.System,
            NullLogger<InvocationResumeRegistry>.Instance,
            budget is null ? null : Options.Create(budget));
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
