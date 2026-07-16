namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Live-invocation tracker backing reconnect/resume. It mirrors the dispatcher's
///     <see cref="IWorkerEventDispatcher.InvocationStateChanged" /> stream into a per-invocation snapshot + state
///     fan-out so a reconnecting client can re-attach with a fresh consumer, and mirrors
///     <see cref="IWorkerEventDispatcher.ToolCallLifecycleChanged" /> so the resumed stream carries the same
///     tool-call timeline the original stream did. It owns no agent logic — it only translates
///     <see cref="InvocationState" /> snapshots and <see cref="ToolCallLifecyclePayload" />s into
///     <see cref="ChatStreamEvent" />s the same way the local pump does.
///     Resume streams number their events from zero with their own counter; the client rebases them onto the
///     original stream's sequence space at the reconnect boundary (NodeChatAdapter), so the registry must only
///     guarantee that its own events are contiguous and ascending.
/// </summary>
public sealed class InvocationResumeRegistry : IInvocationResumeRegistry
{
    private readonly ConcurrentDictionary<Guid, LiveInvocation> _live = new();
    private readonly ILogger<InvocationResumeRegistry> _logger;
    private readonly TimeProvider _timeProvider;

    public InvocationResumeRegistry(IWorkerEventDispatcher eventDispatcher,
        TimeProvider timeProvider,
        ILogger<InvocationResumeRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(eventDispatcher);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Subscribe for the process lifetime; the registry singleton lives as long as the dispatcher singleton,
        // so there is no unsubscribe path (mirrors WorkerEventDispatcher's CA1001 suppression rationale).
        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        eventDispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
        eventDispatcher.TurnNoticeChanged += OnTurnNoticeChanged;
    }

    public InvocationState? TryGetLiveInvocation(Guid invocationId)
    {
        return _live.TryGetValue(invocationId, out var live) && IsNonTerminal(live.LatestState.Status)
            ? Clone(live.LatestState)
            : null;
    }

    public IAsyncEnumerable<ChatStreamEvent> ResumeAsync(Guid invocationId,
        CancellationToken cancellationToken = default)
    {
        if (!_live.TryGetValue(invocationId, out var live) || !IsNonTerminal(live.LatestState.Status))
        {
            throw new InvalidOperationException($"Invocation {invocationId} is not resumable. It is unknown or has already reached a terminal state.");
        }

        return ResumeCoreAsync(live, cancellationToken);
    }

    private async IAsyncEnumerable<ChatStreamEvent> ResumeCoreAsync(LiveInvocation live,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var reader = live.Subscribe(out var snapshot, out var toolHistory, out var noticeHistory);
        var lastContent = snapshot.StreamedContent;
        var lastReasoning = snapshot.StreamedThinkingContent;
        var sequence = 0L;

        // Replay the tool-call timeline, then the notice timeline, accumulated so far, then the content accumulated
        // so far, so the reconnecting client renders the in-flight assistant message (tool cards and notices
        // included) immediately before live items continue in order. The content replay is a pure SNAPSHOT event
        // (full Content/Reasoning, no delta fields): the client applies Content as a replacement, and a delta here
        // would be appended to whatever the client already rendered before the reconnect, duplicating it.
        foreach (var toolCall in toolHistory)
        {
            yield return ToToolCallEvent(snapshot, toolCall, sequence++);
        }

        foreach (var notice in noticeHistory)
        {
            yield return ToNoticeEvent(snapshot, notice, sequence++);
        }

        yield return ToEvent(ChatStreamEventTypes.AssistantDelta, snapshot, sequence++);

        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.ToolCall is { } toolCall)
                {
                    yield return ToToolCallEvent(live.LatestState, toolCall, sequence++);
                    continue;
                }

                if (item.Notice is { } notice)
                {
                    yield return ToNoticeEvent(live.LatestState, notice, sequence++);
                    continue;
                }

                if (item.State is not { } state)
                {
                    continue;
                }

                var hasContentDelta = state.StreamedContent.Length > lastContent.Length;
                var hasReasoningDelta = state.StreamedThinkingContent.Length > lastReasoning.Length;

                if (hasContentDelta || hasReasoningDelta)
                {
                    var contentDelta = hasContentDelta ? state.StreamedContent[lastContent.Length..] : null;
                    var reasoningDelta = hasReasoningDelta ? state.StreamedThinkingContent[lastReasoning.Length..] : null;
                    lastContent = state.StreamedContent;
                    lastReasoning = state.StreamedThinkingContent;

                    yield return ToEvent(ChatStreamEventTypes.AssistantDelta, state, sequence++, contentDelta, reasoningDelta);
                }

                if (TryMapTerminal(state.Status, out var terminalType, out var terminalStatus))
                {
                    yield return ToEvent(terminalType,
                        state,
                        sequence,
                        status: terminalStatus,
                        inputTokens: state.InputTokens,
                        outputTokens: state.OutputTokens,
                        totalTokens: state.TotalTokens,
                        reasoningTokens: state.ReasoningTokens);
                    yield break;
                }
            }
        }
        finally
        {
            live.Unsubscribe(reader);
        }
    }

    private void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        var state = args.State;
        var terminal = IsTerminal(state.Status);

        if (!terminal)
        {
            var live = _live.GetOrAdd(state.InvocationId, _ => new LiveInvocation(state));
            live.Publish(state);
            return;
        }

        // Terminal: fan the terminal state out to any attached resume consumers so they observe it, then
        // remove the entry. A subsequent resume request finds nothing and the client re-fetches the persisted
        // (terminalized) conversation instead.
        if (_live.TryRemove(state.InvocationId, out var existing))
        {
            existing.Publish(state);
            existing.Complete();
            _logger.LogDebug("Resume registry released terminal invocation {InvocationId} with status {Status}.",
                state.InvocationId,
                state.Status);
        }
    }

    private void OnToolCallLifecycleChanged(object? sender, ToolCallLifecycleChangedEventArgs args)
    {
        // Tool lifecycle without a tracked live invocation means the run never reported Assigned/Running (or is
        // already terminal); there is nothing to fan out and nothing to record — the persisted parts[] remain the
        // source of truth on reload.
        if (_live.TryGetValue(args.Payload.InvocationId, out var live))
        {
            live.PublishToolCall(args.Payload);
        }
    }

    private void OnTurnNoticeChanged(object? sender, TurnNoticeChangedEventArgs args)
    {
        // Mirrors OnToolCallLifecycleChanged: a notice without a tracked live invocation has nothing to fan out or
        // record — the persisted parts[] remain the source of truth on reload.
        if (_live.TryGetValue(args.Payload.InvocationId, out var live))
        {
            live.PublishNotice(args.Payload);
        }
    }

    private ChatStreamEvent ToEvent(string type,
        InvocationState state,
        long sequence,
        string? delta = null,
        string? reasoningDelta = null,
        string? status = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        int? reasoningTokens = null)
    {
        return new ChatStreamEvent(type,
            state.ConversationId,
            state.InvocationId,
            state.InvocationId,
            status ?? MapStatus(state.Status),
            sequence,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            delta,
            reasoningDelta,
            state.StreamedContent,
            string.IsNullOrEmpty(state.StreamedThinkingContent) ? null : state.StreamedThinkingContent,
            state.Error,
            state.ModelUsed,
            inputTokens,
            outputTokens,
            totalTokens,
            reasoningTokens);
    }

    private ChatStreamEvent ToToolCallEvent(InvocationState state,
        ToolCallLifecyclePayload payload,
        long sequence)
    {
        // Route through the same mapper the live send/regenerate paths use so a resumed stream's tool-call events are
        // wire-identical to the ones the original stream emitted. Resume events stamp the invocation id as BOTH the
        // message id and the request id; the client remaps them to the assistant message id at the reconnect boundary.
        return ChatStreamEventMapper.ToolCallEvent(state.ConversationId,
            state.InvocationId,
            state.InvocationId,
            payload,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            sequence);
    }

    private ChatStreamEvent ToNoticeEvent(InvocationState state,
        TurnNoticePayload payload,
        long sequence)
    {
        // Mirrors ToToolCallEvent: routes through the same mapper the live send/regenerate paths use so a resumed
        // stream's notice events are wire-identical to the ones the original stream emitted.
        return ChatStreamEventMapper.NoticeEvent(state.ConversationId,
            state.InvocationId,
            state.InvocationId,
            payload,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            sequence);
    }

    private static bool TryMapTerminal(InvocationStatus status,
        out string eventType,
        out string terminalStatus)
    {
        switch (status)
        {
            case InvocationStatus.Completed:
                eventType = ChatStreamEventTypes.AssistantCompleted;
                terminalStatus = NodeChatMessageStatusValues.Completed;
                return true;
            case InvocationStatus.Cancelled:
                eventType = ChatStreamEventTypes.AssistantCancelled;
                terminalStatus = NodeChatMessageStatusValues.Cancelled;
                return true;
            case InvocationStatus.Failed:
                eventType = ChatStreamEventTypes.AssistantFailed;
                terminalStatus = NodeChatMessageStatusValues.Failed;
                return true;
            default:
                eventType = string.Empty;
                terminalStatus = string.Empty;
                return false;
        }
    }

    private static string MapStatus(InvocationStatus status)
    {
        return status switch
        {
            InvocationStatus.Completed => NodeChatMessageStatusValues.Completed,
            InvocationStatus.Cancelled => NodeChatMessageStatusValues.Cancelled,
            InvocationStatus.Failed => NodeChatMessageStatusValues.Failed,
            _ => NodeChatMessageStatusValues.Streaming
        };
    }

    private static bool IsNonTerminal(InvocationStatus status)
    {
        return status is InvocationStatus.Assigned or InvocationStatus.Running;
    }

    private static bool IsTerminal(InvocationStatus status)
    {
        return status is InvocationStatus.Completed or InvocationStatus.Cancelled or InvocationStatus.Failed;
    }

    private static InvocationState Clone(InvocationState state)
    {
        return new InvocationState
        {
            InvocationId = state.InvocationId,
            ConversationId = state.ConversationId,
            Status = state.Status,
            RuntimePhase = state.RuntimePhase,
            StreamedContent = state.StreamedContent,
            StreamedChunkCount = state.StreamedChunkCount,
            StreamedThinkingContent = state.StreamedThinkingContent,
            StreamedThinkingChunkCount = state.StreamedThinkingChunkCount,
            StartedAt = state.StartedAt,
            LastUpdatedAt = state.LastUpdatedAt,
            CompletedAt = state.CompletedAt,
            Error = state.Error,
            FailureCategory = state.FailureCategory,
            ModelUsed = state.ModelUsed,
            InputTokens = state.InputTokens,
            OutputTokens = state.OutputTokens,
            TotalTokens = state.TotalTokens,
            ReasoningTokens = state.ReasoningTokens,
            GenerationDurationMs = state.GenerationDurationMs,
            PendingApproval = state.PendingApproval,
            LastApprovalResolution = state.LastApprovalResolution,
            PendingToolCalls = [.. state.PendingToolCalls],
            LastToolCallResult = state.LastToolCallResult
        };
    }

    /// <summary>
    ///     One item on a resume consumer's channel: exactly one of a state snapshot, a tool-call lifecycle
    ///     transition, or a turn notice, in dispatcher order.
    /// </summary>
    private readonly record struct ResumeItem(InvocationState? State, ToolCallLifecyclePayload? ToolCall, TurnNoticePayload? Notice)
    {
        public static ResumeItem FromState(InvocationState state)
        {
            return new ResumeItem(state, null, null);
        }

        public static ResumeItem FromToolCall(ToolCallLifecyclePayload toolCall)
        {
            return new ResumeItem(null, toolCall, null);
        }

        public static ResumeItem FromNotice(TurnNoticePayload notice)
        {
            return new ResumeItem(null, null, notice);
        }
    }

    /// <summary>
    ///     One live invocation: the latest snapshot, the tool-call timeline so far, and the set of attached resume
    ///     consumers. Each consumer gets its own unbounded channel so a slow reader never blocks the dispatcher's
    ///     publish path. History append and subscriber registration share one lock, so every tool event lands in a
    ///     consumer's replayed history XOR on its channel — never both, never neither.
    /// </summary>
    private sealed class LiveInvocation(InvocationState initialState)
    {
        // Caps the replayed tool timeline for pathological turns; the iteration cap bounds real turns far below
        // this. When exceeded the oldest entries drop — the terminal-gated refetch restores the full persisted
        // timeline anyway.
        private const int MaxRecordedToolEvents = 256;

        // A turn notice fires at most a handful of times (one model substitution, a few distinct tools disabled,
        // one truncation warning); this cap is generous headroom, not a real bound.
        private const int MaxRecordedNoticeEvents = 64;

        private readonly List<ToolCallLifecyclePayload> _toolHistory = [];
        private readonly List<TurnNoticePayload> _noticeHistory = [];
        private readonly List<Channel<ResumeItem>> _subscribers = [];
        private readonly Lock _syncRoot = new();

        // The dispatcher hands every InvocationStateChanged subscriber a fresh, never-subsequently-mutated snapshot
        // (see WorkerEventDispatcher.PublishStateChanged), so a published state is effectively immutable: it can be
        // stored and fanned out by reference without a defensive copy. LatestState stays correct for a late resumer
        // because each publish swaps in the newest snapshot, and a zero-subscriber publish (the common case) does no
        // copying at all.
        public InvocationState LatestState { get; private set; } = initialState;

        public void Publish(InvocationState state)
        {
            lock (_syncRoot)
            {
                LatestState = state;

                foreach (var subscriber in _subscribers)
                {
                    _ = subscriber.Writer.TryWrite(ResumeItem.FromState(state));
                }
            }
        }

        public void PublishToolCall(ToolCallLifecyclePayload toolCall)
        {
            lock (_syncRoot)
            {
                _toolHistory.Add(toolCall);
                if (_toolHistory.Count > MaxRecordedToolEvents)
                {
                    _toolHistory.RemoveAt(0);
                }

                foreach (var subscriber in _subscribers)
                {
                    _ = subscriber.Writer.TryWrite(ResumeItem.FromToolCall(toolCall));
                }
            }
        }

        public void PublishNotice(TurnNoticePayload notice)
        {
            lock (_syncRoot)
            {
                _noticeHistory.Add(notice);
                if (_noticeHistory.Count > MaxRecordedNoticeEvents)
                {
                    _noticeHistory.RemoveAt(0);
                }

                foreach (var subscriber in _subscribers)
                {
                    _ = subscriber.Writer.TryWrite(ResumeItem.FromNotice(notice));
                }
            }
        }

        public ChannelReader<ResumeItem> Subscribe(out InvocationState snapshot,
            out IReadOnlyList<ToolCallLifecyclePayload> toolHistory,
            out IReadOnlyList<TurnNoticePayload> noticeHistory)
        {
            var channel = Channel.CreateUnbounded<ResumeItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            lock (_syncRoot)
            {
                snapshot = Clone(LatestState);
                toolHistory = [.. _toolHistory];
                noticeHistory = [.. _noticeHistory];
                _subscribers.Add(channel);
            }

            return channel.Reader;
        }

        public void Unsubscribe(ChannelReader<ResumeItem> reader)
        {
            lock (_syncRoot)
            {
                var index = _subscribers.FindIndex(channel => ReferenceEquals(channel.Reader, reader));
                if (index < 0)
                {
                    return;
                }

                _subscribers[index].Writer.TryComplete();
                _subscribers.RemoveAt(index);
            }
        }

        public void Complete()
        {
            lock (_syncRoot)
            {
                foreach (var subscriber in _subscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                _subscribers.Clear();
            }
        }
    }
}
