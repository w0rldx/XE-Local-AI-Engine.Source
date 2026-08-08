namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Models;
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

        // A snapshot too large to replay reconciles instead: the client refetches the persisted conversation, which
        // holds the same text, for one request. Deliberately not a TRUNCATED snapshot — truncating would invent a
        // partial-replacement semantic the protocol does not have, and every reader of it would have to know.
        if (lastContent.Length + lastReasoning.Length > _options.MaxReplaySnapshotChars)
        {
            live.Unsubscribe(subscriber);
            yield return ReconcileEvent(snapshot, sequence, "replay_cap");
            yield break;
        }

        // The request ids of the pending prompts (question / tool approval) this resume stream has already emitted.
        // Every state publish carries the still-pending slot, so without this the prompt would be re-emitted on every
        // delta; a request id is minted once per prompt, so first-seen-wins is exact. Scoped to this consumer, so two
        // concurrently reconnected browsers each get the prompt exactly once.
        var replayedPrompts = new HashSet<string>(StringComparer.Ordinal);

        // Replay the tool-call timeline, then the notice timeline, accumulated so far, then the content accumulated
        // so far, so the reconnecting client renders the in-flight assistant message (tool cards and notices
        // included) immediately before live items continue in order. The content replay is an AssistantSnapshot
        // (full Content/Reasoning, no delta fields): the client applies Content as a replacement, and a delta here
        // would be appended to whatever the client already rendered before the reconnect, duplicating it. It is also
        // what resets the client's delta-offset counters, which is why the same event serves gap and overflow repair —
        // both reach it by re-subscribing through this method.
        foreach (var toolCall in toolHistory)
        {
            yield return ToToolCallEvent(snapshot, toolCall, sequence++);
        }

        foreach (var notice in noticeHistory)
        {
            yield return ToNoticeEvent(snapshot, notice, sequence++);
        }

        // Then any prompt the turn is currently PARKED on, after the tool timeline that carries the card it attaches
        // to. Without this a mid-turn reload permanently loses the controls and the run stays blocked until it times
        // out — fatal for a question, which the turn cannot proceed without.
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

        try
        {
            // The invocation can go terminal in the window between ResumeAsync's non-terminal validation and
            // this first Subscribe. When it does, OnInvocationStateChanged runs Publish(terminal) then Complete() before
            // our channel is (or right as it is) registered — so the live loop below would never carry a terminal event
            // (the channel is already completed, or was completed by Complete()). The snapshot taken under Subscribe's
            // lock already reflects that terminal, so emit it directly and finish rather than ending the resume stream
            // with no terminal (which would leave the consumer waiting for a terminal that never arrives).
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

            await foreach (var item in subscriber.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // This consumer's own queue overflowed, so its stream is no longer contiguous — an approval or a tool
                // result may have been the item that fell off. Tell it to re-resume rather than guessing which kind
                // was safe to lose.
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

                var hasContentDelta = state.StreamedContent.Length > lastContent.Length;
                var hasReasoningDelta = state.StreamedThinkingContent.Length > lastReasoning.Length;

                if (hasContentDelta || hasReasoningDelta)
                {
                    // The slice bases ARE the wire offsets: the client appends each delta at the offset it expected and
                    // re-resumes if they do not line up. Both offsets ride every delta even when only one side
                    // advanced, so a stalled side still confirms its position.
                    var contentOffset = lastContent.Length;
                    var reasoningOffset = lastReasoning.Length;
                    var contentDelta = hasContentDelta ? state.StreamedContent[contentOffset..] : null;
                    var reasoningDelta = hasReasoningDelta ? state.StreamedThinkingContent[reasoningOffset..] : null;
                    lastContent = state.StreamedContent;
                    lastReasoning = state.StreamedThinkingContent;

                    yield return ChatStreamEventMapper.DeltaEvent(new NodeChatMessageCorrelation(state.ConversationId, state.InvocationId, state.InvocationId),
                        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        sequence++,
                        contentDelta,
                        reasoningDelta,
                        contentOffset,
                        reasoningOffset);
                }

                // A prompt raised while this resume stream is attached arrives as a state publish (the dispatcher
                // records the pending slot BEFORE fanning the live event out), so the same dedupe covers both the
                // snapshot replay above and the live case here — a prompt is emitted exactly once per consumer.
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
    ///     whose first frame is an authoritative snapshot. Raised when the consumer's own queue overflowed or when the
    ///     replay snapshot was too large to send. Silent to the user by design — the counter is the only signal.
    /// </summary>
    private ChatStreamEvent ReconcileEvent(InvocationState state, long sequence, string reason)
    {
        NodeMetrics.ChatStreamReconcileTotal.Add(1, new KeyValuePair<string, object?>("reason", reason));

        return ChatStreamEventMapper.ReconcileEvent(new NodeChatMessageCorrelation(state.ConversationId, state.InvocationId, state.InvocationId),
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            sequence);
    }

    private void OnInvocationStateChanged(object? sender, InvocationStateChangedEventArgs args)
    {
        var state = args.State;
        var terminal = IsTerminal(state.Status);

        if (!terminal)
        {
            // TryGetValue first: this runs once per streamed token, and every publish after the first finds the entry
            // already there. The GetOrAdd fallback takes the state as a factory ARGUMENT so the miss path does not
            // allocate a capturing closure either.
            if (!_live.TryGetValue(state.InvocationId, out var live))
            {
                live = _live.GetOrAdd(state.InvocationId,
                    static (_, arg) => new LiveInvocation(arg.InitialState, arg.Options),
                    (InitialState: state, Options: _options));
            }

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

    /// <summary>
    ///     Builds a TERMINAL event from a live state — the only remaining use for a full-content event on this path.
    ///     Content and reasoning are carried unconditionally: a terminal is one frame per turn, so its cost is
    ///     irrelevant, and it doubles as the backstop that converges any client whose delta stream fell behind.
    ///     Deltas go through <see cref="ChatStreamEventMapper.DeltaEvent" /> and the opening replay through
    ///     <see cref="ChatStreamEventMapper.SnapshotEvent" />; neither may be built here.
    /// </summary>
    private ChatStreamEvent ToEvent(string type,
        InvocationState state,
        long sequence,
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
            Content: state.StreamedContent,
            Reasoning: string.IsNullOrEmpty(state.StreamedThinkingContent) ? null : state.StreamedThinkingContent,
            Error: state.Error,
            Model: state.ModelUsed,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            TotalTokens: totalTokens,
            ReasoningTokens: reasoningTokens);
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

    /// <summary>
    ///     Builds the replay events for whatever human prompt the invocation is currently parked on — a pending
    ///     <c>ask_user</c> question and/or a pending tool approval — skipping any this consumer already received.
    ///     Returns an empty list on the overwhelmingly common no-prompt path.
    ///     <para>
    ///         The pending slots are the ONLY outward surface a reconnecting browser has for these prompts: neither
    ///         event is accumulated into the persisted <c>parts[]</c> (both are transient live state), and the live
    ///         <c>ApprovalRequestedChanged</c>/<c>UserQuestionRequestedChanged</c> fan-out reached only the original
    ///         stream, which the reload tore down. Both events route through the same
    ///         <see cref="ChatStreamEventMapper" /> the live paths use, so a replayed prompt is wire-identical to a
    ///         live one; a resume stream stamps the invocation id as both the message id and the request id, as it
    ///         does for tool-call and notice replay.
    ///     </para>
    /// </summary>
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

        // The approval slot's CallId/ToolName are optional (a platform-hub approval carries neither). The mapper maps a
        // blank to a null wire field, so the client can still render the prompt — it just cannot attach it to a
        // specific tool-call card. Populating them for a locally-raised approval is the runner's job.
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
                    Description = approval.Description
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
    ///     One live invocation: the latest snapshot, the tool-call timeline so far, and the set of attached resume
    ///     consumers. Each consumer gets its own BOUNDED channel so a slow reader never blocks the dispatcher's
    ///     publish path and never grows without limit either — a browser that reconnects but stops reading used to
    ///     retain every state publish for the rest of the run. History append and subscriber registration share one
    ///     lock, so every tool event lands in a consumer's replayed history XOR on its channel — never both, never
    ///     neither.
    /// </summary>
    private sealed class LiveInvocation(InvocationState initialState, ChatStreamBudgetOptions options)
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
        private readonly List<ResumeSubscriber> _subscribers = [];
        private readonly Lock _syncRoot = new();

        // Latched under _syncRoot when Complete() runs (the terminal state has been published and the then-attached
        // subscribers completed). A Subscribe that races in AFTER Complete would otherwise register a channel that no
        // future publish/complete will ever finish. Once set it never clears; the entry is removed from the
        // registry at the same time, so no non-terminal publish can follow.
        private bool _completed;

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
        ///     consumer rather than evicting an existing one: a runaway reconnect loop in one tab must never knock a
        ///     working browser off its own stream. The rejected caller sees the same failure shape as "not resumable"
        ///     and falls back to refetching the persisted conversation.
        /// </exception>
        public ResumeSubscriber Subscribe(out InvocationState snapshot,
            out IReadOnlyList<ToolCallLifecyclePayload> toolHistory,
            out IReadOnlyList<TurnNoticePayload> noticeHistory)
        {
            var subscriber = new ResumeSubscriber(options.QueueCapacity);

            lock (_syncRoot)
            {
                snapshot = LatestState.Clone();
                toolHistory = [.. _toolHistory];
                noticeHistory = [.. _noticeHistory];

                // The invocation already reached its terminal and Complete() ran (which completed and cleared
                // the then-attached subscribers). Registering a channel now would leave a reader that no future
                // publish/complete ever finishes, so hand back an already-completed channel. The snapshot above is the
                // terminal state (Publish precedes Complete under this same lock), which ResumeCoreAsync emits directly.
                if (_completed)
                {
                    subscriber.Complete();
                }
                else if (_subscribers.Count >= options.MaxSubscribersPerInvocation)
                {
                    throw new InvalidOperationException($"Invocation {LatestState.InvocationId} already has the maximum of {options.MaxSubscribersPerInvocation} resume subscribers.");
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
    ///     One attached resume consumer: its bounded queue plus the latch that records whether that queue overflowed.
    ///     The queue drops rather than waiting, because it is written under <c>LiveInvocation</c>'s lock on the
    ///     dispatcher's publish path — a wait there would stall every other consumer AND the run itself. What the drop
    ///     costs is repaired at the stream level: the consumer is told to re-resume, exactly as an overflowing live
    ///     stream is.
    /// </summary>
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
