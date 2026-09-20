namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Live-invocation tracker backing reconnect and resume.
/// </summary>
/// <remarks>
///     It mirrors <see cref="IWorkerEventDispatcher.InvocationStateChanged" /> into a per-invocation snapshot and
///     state fan-out so a reconnecting client re-attaches with a fresh consumer, and
///     <see cref="IWorkerEventDispatcher.ToolCallLifecycleChanged" /> so the resumed stream carries the original
///     tool-call timeline. It owns no agent logic. Resume streams number their events from zero with their own
///     counter, which the client rebases at the reconnect boundary, so only contiguity and ascent are guaranteed.
/// </remarks>
public sealed class InvocationResumeRegistry : IInvocationResumeRegistry
{
    private readonly ConcurrentDictionary<Guid, LiveInvocation> _live = new();
    private readonly ILogger<InvocationResumeRegistry> _logger;
    private readonly ChatStreamBudgetOptions _options;
    private readonly TimeProvider _timeProvider;

    // The budget is optional so the many direct constructions in tests keep the shipped defaults without threading
    // options through, mirroring ChatInvocationStatePump.
    public InvocationResumeRegistry(IWorkerEventDispatcher eventDispatcher,
        TimeProvider timeProvider,
        ILogger<InvocationResumeRegistry> logger,
        IOptions<ChatStreamBudgetOptions>? options = null)
    {
        ArgumentNullException.ThrowIfNull(eventDispatcher);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? new ChatStreamBudgetOptions();

        // Subscribe for the process lifetime; the registry singleton lives as long as the dispatcher singleton,
        // so there is no unsubscribe path (mirrors WorkerEventDispatcher's CA1001 suppression rationale).
        eventDispatcher.InvocationStateChanged += OnInvocationStateChanged;
        eventDispatcher.ToolCallLifecycleChanged += OnToolCallLifecycleChanged;
        eventDispatcher.TurnNoticeChanged += OnTurnNoticeChanged;
    }

    public InvocationState? TryGetLiveInvocation(Guid invocationId)
    {
        return _live.TryGetValue(invocationId, out var live) && IsNonTerminal(live.LatestState.Status)
            ? live.LatestState.Clone()
            : null;
    }

    public Guid? TryGetLiveInvocationIdForConversation(Guid conversationId)
    {
        foreach (var state in _live.Values.Select(live => live.LatestState))
        {
            if (state.ConversationId == conversationId && IsNonTerminal(state.Status))
            {
                return state.InvocationId;
            }
        }

        return null;
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
        var subscriber = live.Subscribe(out var snapshot, out var toolHistory, out var noticeHistory);
        var lastContent = snapshot.StreamedContent;
        var lastReasoning = snapshot.StreamedThinkingContent;
        var sequence = 0L;

        // A snapshot too large to replay reconciles instead, so the client refetches the persisted conversation. A
        // TRUNCATED snapshot is deliberately not used: it would invent a partial-replacement semantic the wire lacks.
        if (lastContent.Length + lastReasoning.Length > _options.MaxReplaySnapshotChars)
        {
            live.Unsubscribe(subscriber);
            yield return ReconcileEvent(snapshot, sequence, "replay_cap");
            yield break;
        }

        // The request ids of the pending prompts this stream already emitted: every state publish carries the still-
        // pending slot, and an id is minted once per prompt, so first-seen-wins is exact and scoped to this consumer.
        var replayedPrompts = new HashSet<string>(StringComparer.Ordinal);

        // Replay the tool-call timeline, the notices, then the content, before live items continue. That content is an
        // AssistantSnapshot applied as a REPLACEMENT, and it resets the delta offsets, repairing a gap or an overflow.
        foreach (var toolCall in toolHistory)
        {
            yield return ToToolCallEvent(snapshot, toolCall, sequence++);
        }

        foreach (var notice in noticeHistory)
        {
            yield return ToNoticeEvent(snapshot, notice, sequence++);
        }

        // Then any prompt the turn is PARKED on, after the tool timeline carrying the card it attaches to, or a
        // mid-turn reload loses the controls and the run stays blocked until it times out.
        foreach (var promptEvent in BuildPendingPromptEvents(snapshot, replayedPrompts, sequence))
        {
            yield return promptEvent;
            sequence++;
        }

        yield return ChatStreamEventMapper.SnapshotEvent(snapshot.ConversationId,
            snapshot.InvocationId,
            snapshot.InvocationId,
            lastContent,
            string.IsNullOrEmpty(lastReasoning) ? null : lastReasoning,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            sequence++);

        // The last runtime phase this stream surfaced. ONE mechanism serves the opening replay and the live loop, so
        // they cannot drift, and a state publish that changed only content does not re-emit the phase.
        InvocationRuntimePhase? lastEmittedPhase = null;

        // Always the ORIGINAL change time off the state, never a fresh stamp, or the reloading client's elapsed timer
        // resets to zero. timestampMs is the FRAME's send time and stays on the registry's own clock.
        ChatStreamEvent PhaseEventFor(InvocationState phaseState, InvocationRuntimePhase phase, long phaseSequence)
        {
            return ChatStreamEventMapper.PhaseEvent(new NodeChatMessageCorrelation { ConversationId = phaseState.ConversationId, MessageId = phaseState.InvocationId, RequestId = phaseState.InvocationId },
                phase,
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                phaseSequence,
                phaseState.RuntimePhaseChangedAtUtc);
        }

        // Replayed AFTER the snapshot, because the client's snapshot reducer arm rebuilds the streaming message and
        // would discard an earlier phase, leaving a cold-load reload with no affordance and no elapsed time.
        if (IsNonTerminal(snapshot.Status) && snapshot.RuntimePhase is { } resumedPhase)
        {
            lastEmittedPhase = resumedPhase;
            yield return PhaseEventFor(snapshot, resumedPhase, sequence++);
        }

        try
        {
            // The invocation can go terminal between ResumeAsync's validation and this Subscribe, completing the
            // channel; the snapshot taken under its lock reflects it, so emit it rather than end with no terminal.
            if (TryMapTerminal(snapshot.Status, out var snapshotTerminalType, out var snapshotTerminalStatus))
            {
                yield return ToEvent(snapshotTerminalType,
                    snapshot,
                    sequence,
                    status: snapshotTerminalStatus,
                    inputTokens: snapshot.InputTokens,
                    outputTokens: snapshot.OutputTokens,
                    totalTokens: snapshot.TotalTokens,
                    reasoningTokens: snapshot.ReasoningTokens);
                yield break;
            }

            await foreach (var item in subscriber.Reader.ReadAllAsync(cancellationToken))
            {
                // This consumer's queue overflowed, so its stream is no longer contiguous and the lost item may have
                // been an approval or a tool result. Tell it to re-resume rather than guess what was safe to lose.
                if (subscriber.TryConsumeReconcile())
                {
                    yield return ReconcileEvent(live.LatestState, sequence++, "queue_overflow");
                }

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

                // Before the delta block, so a phase change arriving with the first content still precedes it: a
                // reconnect while queued would otherwise show no phase, and one during a load miss Generating.
                if (IsNonTerminal(state.Status) && state.RuntimePhase is { } livePhase && livePhase != lastEmittedPhase)
                {
                    lastEmittedPhase = livePhase;
                    yield return PhaseEventFor(state, livePhase, sequence++);
                }

                var hasContentDelta = state.StreamedContent.Length > lastContent.Length;
                var hasReasoningDelta = state.StreamedThinkingContent.Length > lastReasoning.Length;

                if (hasContentDelta || hasReasoningDelta)
                {
                    // The slice bases ARE the wire offsets: the client appends at the offset it expected and re-resumes
                    // if they disagree. Both ride every delta even when one side stalled, so it confirms its position.
                    var contentOffset = lastContent.Length;
                    var reasoningOffset = lastReasoning.Length;
                    var contentDelta = hasContentDelta ? state.StreamedContent[contentOffset..] : null;
                    var reasoningDelta = hasReasoningDelta ? state.StreamedThinkingContent[reasoningOffset..] : null;
                    lastContent = state.StreamedContent;
                    lastReasoning = state.StreamedThinkingContent;

                    yield return ChatStreamEventMapper.DeltaEvent(new NodeChatMessageCorrelation { ConversationId = state.ConversationId, MessageId = state.InvocationId, RequestId = state.InvocationId },
                        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        sequence++,
                        contentDelta,
                        reasoningDelta,
                        contentOffset,
                        reasoningOffset);
                }

                // A prompt raised while this stream is attached arrives as a state publish, because the dispatcher
                // records the pending slot BEFORE fanning out, so the one dedupe covers replay and live alike.
                foreach (var promptEvent in BuildPendingPromptEvents(state, replayedPrompts, sequence))
                {
                    yield return promptEvent;
                    sequence++;
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
            live.Unsubscribe(subscriber);
        }
    }

    /// <summary>
    ///     Tells this consumer to resynchronize: dispose the subscription and re-enter through <c>ResumeMessage</c>,
    ///     whose first frame is an authoritative snapshot.
    /// </summary>
    /// <remarks>
    ///     Raised when the consumer's own queue overflowed or when the replay snapshot was too large to send. Silent
    ///     to the user by design — the counter is the only signal.
    /// </remarks>
    private ChatStreamEvent ReconcileEvent(InvocationState state, long sequence, string reason)
    {
        NodeMetrics.ChatStreamReconcileTotal.Add(1, new KeyValuePair<string, object?>("reason", reason));

        return ChatStreamEventMapper.ReconcileEvent(new NodeChatMessageCorrelation { ConversationId = state.ConversationId, MessageId = state.InvocationId, RequestId = state.InvocationId },
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            sequence);
    }

    private void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        var state = args.State;
        var terminal = IsTerminal(state.Status);

        if (!terminal)
        {
            // TryGetValue first: this runs once per streamed token and every publish after the first hits. The GetOrAdd
            // fallback takes the state as a factory ARGUMENT so the miss path allocates no capturing closure either.
            if (!_live.TryGetValue(state.InvocationId, out var live))
            {
                live = _live.GetOrAdd(state.InvocationId,
                    static (_, arg) => new LiveInvocation(arg.InitialState, arg.Options),
                    (InitialState: state, Options: _options));
            }

            live.Publish(state);
            return;
        }

        // Terminal: fan the state out to any attached consumers so they observe it, then remove the entry. A later
        // resume request finds nothing and the client refetches the persisted, terminalized conversation.
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
        // Tool lifecycle without a tracked live invocation means the run never reported Assigned or Running, or is
        // already terminal: nothing to fan out, and the persisted parts[] remain the source of truth on reload.
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

    /// <summary>
    ///     Builds a TERMINAL event from a live state, the only remaining use for a full-content event on this path.
    /// </summary>
    /// <remarks>
    ///     Content and reasoning ride it unconditionally: a terminal is one frame per turn, and it doubles as the
    ///     backstop that converges any client whose delta stream fell behind. Deltas go through
    ///     <see cref="ChatStreamEventMapper.DeltaEvent" /> and the opening replay through
    ///     <see cref="ChatStreamEventMapper.SnapshotEvent" />; neither may be built here.
    /// </remarks>
    private ChatStreamEvent ToEvent(string type,
        InvocationState state,
        long sequence,
        string? status = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        int? reasoningTokens = null)
    {
        return new ChatStreamEvent
        {
            Type = type,
            ConversationId = state.ConversationId,
            MessageId = state.InvocationId,
            RequestId = state.InvocationId,
            Status = status ?? MapStatus(state.Status),
            Sequence = sequence,
            OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            Content = state.StreamedContent,
            Reasoning = string.IsNullOrEmpty(state.StreamedThinkingContent) ? null : state.StreamedThinkingContent,
            Error = state.Error,
            Model = state.ModelUsed,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            TotalTokens = totalTokens,
            ReasoningTokens = reasoningTokens
        };
    }

    private ChatStreamEvent ToToolCallEvent(InvocationState state,
        ToolCallLifecyclePayload payload,
        long sequence)
    {
        // Routed through the same mapper the live paths use, so a resumed tool-call event is wire-identical. Resume
        // events stamp the invocation id as BOTH message and request id; the client remaps them on reconnect.
        return ChatStreamEventMapper.ToolCallEvent(state.ConversationId,
            state.InvocationId,
            state.InvocationId,
            payload,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            sequence);
    }

    /// <summary>
    ///     Builds the replay events for whatever human prompt the invocation is parked on — a pending
    ///     <c>ask_user</c> question and/or a pending tool approval — skipping any this consumer already received.
    /// </summary>
    /// <remarks>
    ///     The pending slots are the ONLY outward surface a reconnecting browser has for these prompts: neither is
    ///     accumulated into the persisted <c>parts[]</c>, and the live fan-out reached only the stream the reload tore
    ///     down. Both route through the same <see cref="ChatStreamEventMapper" /> the live paths use, so a replayed
    ///     prompt is wire-identical. The common no-prompt path returns an empty list.
    /// </remarks>
    private List<ChatStreamEvent> BuildPendingPromptEvents(InvocationState state, HashSet<string> replayedPrompts, long sequence)
    {
        if (state.PendingQuestion is null && state.PendingApproval is null)
        {
            return [];
        }

        var events = new List<ChatStreamEvent>(capacity: 2);
        var timestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (state.PendingQuestion is { } question && replayedPrompts.Add(question.RequestId))
        {
            events.Add(ChatStreamEventMapper.QuestionRequestedEvent(state.ConversationId,
                state.InvocationId,
                state.InvocationId,
                new UserQuestionLifecyclePayload
                {
                    InvocationId = state.InvocationId,
                    RequestId = question.RequestId,
                    CallId = question.CallId,
                    ToolName = question.ToolName,
                    Questions = question.Questions
                },
                timestampMs,
                sequence + events.Count));
        }

        // The approval slot's CallId and ToolName are optional, since a platform-hub approval carries neither; the
        // mapper maps a blank to null, so the client renders the prompt unattached to any tool-call card.
        if (state.PendingApproval is { } approval && replayedPrompts.Add(approval.RequestId))
        {
            events.Add(ChatStreamEventMapper.ApprovalRequestedEvent(state.ConversationId,
                state.InvocationId,
                state.InvocationId,
                new ApprovalLifecyclePayload
                {
                    InvocationId = state.InvocationId,
                    RequestId = approval.RequestId,
                    CallId = approval.CallId ?? string.Empty,
                    ToolName = approval.ToolName ?? string.Empty,
                    Description = approval.Description,
                    // Fail CLOSED on a slot that never recorded the runner's answer: a replayed card must not offer a
                    // session scope the node cannot honor, and a one-off approval still works.
                    SessionScopeEligible = approval.SessionScopeEligible ?? false
                },
                timestampMs,
                sequence + events.Count));
        }

        return events;
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
    ///     One live invocation: the latest snapshot, the tool-call timeline so far, and the attached resume consumers.
    /// </summary>
    /// <remarks>
    ///     Each consumer gets its own BOUNDED channel, so a reader that reconnects and then stops reading neither
    ///     blocks the dispatcher's publish path nor retains every state publish for the rest of the run. History
    ///     append and subscriber registration share one lock, so every tool event lands in a consumer's replayed
    ///     history XOR on its channel — never both, never neither.
    /// </remarks>
    private sealed class LiveInvocation
    {
        // Caps the replayed tool timeline for pathological turns; the iteration cap bounds real turns far below it.
        // The oldest entries drop when exceeded, and the terminal-gated refetch restores the persisted timeline.
        private const int MaxRecordedToolEvents = 256;

        // A turn notice fires at most a handful of times (one model substitution, a few distinct tools disabled,
        // one truncation warning); this cap is generous headroom, not a real bound.
        private const int MaxRecordedNoticeEvents = 64;

        private readonly List<ToolCallLifecyclePayload> _toolHistory = [];
        private readonly List<TurnNoticePayload> _noticeHistory = [];
        private readonly List<ResumeSubscriber> _subscribers = [];
        private readonly Lock _syncRoot = new();
        private readonly ChatStreamBudgetOptions _options;

        // Latched under _syncRoot when Complete() runs, or a Subscribe racing in after it would register a channel no
        // future publish will ever finish. It never clears: the registry entry is removed at the same time.
        private bool _completed;

        public LiveInvocation(InvocationState initialState, ChatStreamBudgetOptions options)
        {
            _options = options;
            LatestState = initialState;
        }

        // The dispatcher hands every subscriber a fresh, never-subsequently-mutated snapshot, so a published state is
        // effectively immutable and is stored and fanned out by reference, with no copying on a zero-subscriber publish.
        public InvocationState LatestState { get; private set; }

        public void Publish(InvocationState state)
        {
            lock (_syncRoot)
            {
                LatestState = state;

                foreach (var subscriber in _subscribers)
                {
                    subscriber.Write(ResumeItem.FromState(state));
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
                    subscriber.Write(ResumeItem.FromToolCall(toolCall));
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
                    subscriber.Write(ResumeItem.FromNotice(notice));
                }
            }
        }

        /// <summary>
        ///     Attaches a consumer, returning its own queue plus the history to replay ahead of it.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        ///     More than <c>MaxSubscribersPerInvocation</c> consumers are already attached. The cap REJECTS the new
        ///     consumer rather than evicting an existing one, so a runaway reconnect loop in one tab cannot knock a
        ///     working browser off its stream; the rejected caller falls back to refetching the conversation.
        /// </exception>
        public ResumeSubscriber Subscribe(out InvocationState snapshot,
            out IReadOnlyList<ToolCallLifecyclePayload> toolHistory,
            out IReadOnlyList<TurnNoticePayload> noticeHistory)
        {
            var subscriber = new ResumeSubscriber(_options.QueueCapacity);

            lock (_syncRoot)
            {
                snapshot = LatestState.Clone();
                toolHistory = [.. _toolHistory];
                noticeHistory = [.. _noticeHistory];

                // Complete() already ran, so registering a channel now would leave a reader nothing ever finishes:
                // hand back a completed one. The snapshot above is the terminal state, which ResumeCoreAsync emits.
                if (_completed)
                {
                    subscriber.Complete();
                }
                else if (_subscribers.Count >= _options.MaxSubscribersPerInvocation)
                {
                    throw new InvalidOperationException($"Invocation {LatestState.InvocationId} already has the maximum of {_options.MaxSubscribersPerInvocation} resume subscribers.");
                }
                else
                {
                    _subscribers.Add(subscriber);
                }
            }

            return subscriber;
        }

        public void Unsubscribe(ResumeSubscriber subscriber)
        {
            lock (_syncRoot)
            {
                if (_subscribers.Remove(subscriber))
                {
                    subscriber.Complete();
                }
            }
        }

        public void Complete()
        {
            lock (_syncRoot)
            {
                // Latch terminal so a Subscribe that races in after this point gets an already-completed channel
                // rather than one nothing will ever finish.
                _completed = true;

                foreach (var subscriber in _subscribers)
                {
                    subscriber.Complete();
                }

                _subscribers.Clear();
            }
        }
    }

    /// <summary>
    ///     One attached resume consumer: its bounded queue plus the latch recording whether that queue overflowed.
    /// </summary>
    /// <remarks>
    ///     The queue drops rather than waits, because it is written under <c>LiveInvocation</c>'s lock on the
    ///     dispatcher's publish path, where a wait would stall every other consumer AND the run itself. The drop is
    ///     repaired at the stream level: the consumer is told to re-resume, exactly as an overflowing live stream is.
    /// </remarks>
    private sealed class ResumeSubscriber
    {
        private readonly Channel<ResumeItem> _channel;

        // Read-and-cleared atomically by the consumer, so a burst of drops yields exactly one reconcile.
        private int _reconcileNeeded;

        public ResumeSubscriber(int capacity)
        {
            _channel = Channel.CreateBounded<ResumeItem>(new BoundedChannelOptions(capacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropWrite
                },
                // TryWrite reports SUCCESS for a DropWrite drop, so this callback is the only place the overflow is
                // observable.
                _ => Interlocked.Exchange(ref _reconcileNeeded, value: 1));
        }

        public ChannelReader<ResumeItem> Reader => _channel.Reader;

        public void Write(ResumeItem item)
        {
            _ = _channel.Writer.TryWrite(item);
        }

        public bool TryConsumeReconcile()
        {
            return Interlocked.Exchange(ref _reconcileNeeded, value: 0) == 1;
        }

        public void Complete()
        {
            _channel.Writer.TryComplete();
        }
    }
}
