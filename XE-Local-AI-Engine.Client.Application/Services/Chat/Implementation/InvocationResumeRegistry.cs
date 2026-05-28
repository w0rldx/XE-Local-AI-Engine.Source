namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Live-invocation tracker backing reconnect/resume (Phase 2.2). It mirrors the dispatcher's
///     <see cref="IWorkerEventDispatcher.InvocationStateChanged" /> stream into a per-invocation snapshot + state
///     fan-out so a reconnecting client can re-attach with a fresh consumer. It owns no agent logic — it only
///     translates <see cref="InvocationState" /> snapshots into <see cref="ChatStreamEvent" />s the same way the
///     local pump does.
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
        var reader = live.Subscribe(out var snapshot);
        var lastContent = snapshot.StreamedContent;
        var lastReasoning = snapshot.StreamedThinkingContent;
        var sequence = 0L;

        // Replay the content accumulated so far as a single snapshot delta so the reconnecting client renders
        // the in-flight assistant message immediately, then continues with live deltas in order.
        yield return ToEvent(ChatStreamEventTypes.AssistantDelta,
            snapshot,
            sequence++,
            delta: lastContent.Length == 0 ? null : lastContent,
            reasoningDelta: lastReasoning.Length == 0 ? null : lastReasoning);

        try
        {
            await foreach (var state in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
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
            PendingApproval = state.PendingApproval,
            LastApprovalResolution = state.LastApprovalResolution,
            PendingToolCalls = [.. state.PendingToolCalls],
            LastToolCallResult = state.LastToolCallResult
        };
    }

    /// <summary>
    ///     One live invocation: the latest snapshot plus the set of attached resume consumers. Each consumer gets
    ///     its own unbounded channel so a slow reader never blocks the dispatcher's publish path.
    /// </summary>
    private sealed class LiveInvocation(InvocationState initialState)
    {
        private readonly List<Channel<InvocationState>> _subscribers = [];
        private readonly object _syncRoot = new();

        public InvocationState LatestState { get; private set; } = Clone(initialState);

        public void Publish(InvocationState state)
        {
            lock (_syncRoot)
            {
                LatestState = Clone(state);

                foreach (var subscriber in _subscribers)
                {
                    _ = subscriber.Writer.TryWrite(Clone(state));
                }
            }
        }

        public ChannelReader<InvocationState> Subscribe(out InvocationState snapshot)
        {
            var channel = Channel.CreateUnbounded<InvocationState>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            lock (_syncRoot)
            {
                snapshot = Clone(LatestState);
                _subscribers.Add(channel);
            }

            return channel.Reader;
        }

        public void Unsubscribe(ChannelReader<InvocationState> reader)
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
