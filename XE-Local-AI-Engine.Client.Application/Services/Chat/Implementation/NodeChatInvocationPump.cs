namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Shared per-invocation persistence pump: it consumes one agent run's <see cref="InvocationState" /> deltas and
///     persists them through <see cref="INodeChatPersistenceService" /> for both front doors.
/// </summary>
/// <remarks>
///     It flushes streamed partials and terminalizes the assistant message for the local loopback
///     (<see cref="NodeChatStreamService" />), which additionally turns each persisted state into a
///     <see cref="ChatStreamEvent" />, and for the platform path, which needs only the persistence side. It owns no
///     agent logic and no transport, and is driven one state at a time by the caller. It is the write counterpart to
///     the read-only <see cref="InvocationResumeRegistry" />, which turns the same states into resume events.
/// </remarks>
public sealed class NodeChatInvocationPump : INodeChatInvocationPump
{
    private readonly INodeChatPersistenceService _persistence;
    private readonly IUsageProviderResolver _usageProviderResolver;
    private readonly TimeProvider _timeProvider;

    public NodeChatInvocationPump(INodeChatPersistenceService persistence,
        IUsageProviderResolver usageProviderResolver,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(usageProviderResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _persistence = persistence;
        _usageProviderResolver = usageProviderResolver;
        _timeProvider = timeProvider;
    }

    /// <summary>
    ///     Persists a streamed content or reasoning delta if the incoming state advanced past
    ///     <paramref name="cursor" />.
    /// </summary>
    /// <remarks>
    ///     It returns the updated cursor and, when a delta was persisted, the persisted message plus the raw delta
    ///     slices so the caller can emit a stream event. When nothing advanced,
    ///     <see cref="NodeChatPumpFlushResult.Persisted" /> is null and the cursor is unchanged.
    /// </remarks>
    public async Task<NodeChatPumpFlushResult> FlushDeltaAsync(NodeChatMessageCorrelation correlation,
        InvocationState state,
        NodeChatPumpCursor cursor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var hasContentDelta = state.StreamedContent.Length > cursor.Content.Length;
        var hasReasoningDelta = state.StreamedThinkingContent.Length > cursor.Reasoning.Length;

        if (!hasContentDelta && !hasReasoningDelta)
        {
            return new NodeChatPumpFlushResult
            {
                Cursor = cursor,
                Persisted = null,
                ContentDelta = null,
                ReasoningDelta = null
            };
        }

        var contentDelta = hasContentDelta ? state.StreamedContent[cursor.Content.Length..] : null;
        var reasoningDelta = hasReasoningDelta ? state.StreamedThinkingContent[cursor.Reasoning.Length..] : null;
        var nextCursor = new NodeChatPumpCursor(state.StreamedContent, state.StreamedThinkingContent);

        var persisted = await _persistence.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest
            {
                Correlation = correlation,
                Content = nextCursor.Content,
                Reasoning = string.IsNullOrEmpty(nextCursor.Reasoning) ? null : nextCursor.Reasoning,
                UpdatedAtUtc = NowUnixMilliseconds()
            },
            cancellationToken);

        return new NodeChatPumpFlushResult
        {
            Cursor = nextCursor,
            Persisted = persisted,
            ContentDelta = contentDelta,
            ReasoningDelta = reasoningDelta
        };
    }

    /// <summary>
    ///     Terminalizes the assistant message from a terminal <see cref="InvocationState" />, returning the persisted
    ///     message and the resolved terminal status and event type.
    /// </summary>
    /// <remarks>
    ///     It always persists on <see cref="CancellationToken.None" />, so the terminal row is written even when the
    ///     run was cancelled.
    /// </remarks>
    public async Task<NodeChatPumpTerminalResult> TerminalizeAsync(NodeChatMessageCorrelation correlation,
        InvocationState state,
        string? requestedModel,
        IReadOnlyList<NodeChatMessagePart>? parts = null,
        IReadOnlyList<NodeChatMessageSource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!TryMapTerminal(state.Status, out var terminalStatus, out var eventType))
        {
            throw new ArgumentException($"Invocation status '{state.Status}' is not terminal.", nameof(state));
        }

        // Durable run ledger: the envelope payload rides INTO the terminalize command so its content-free row commits
        // in the SAME transaction as the terminal row. The status and agent id come from the winning row inside it.
        var durationMs = state.GenerationDurationMs
                         ?? (state.CompletedAt is { } completedAt ? Math.Max(val1: 0L, (long)(completedAt - state.StartedAt).TotalMilliseconds) : 0L);

        // Attributes the turn's tokens to the provider that served it, from the same model id that rides into
        // terminalize. The resolver never throws and is bounded, degrading to 'unknown', so it cannot stall the write.
        var provider = await _usageProviderResolver.ResolveAsync(state.ModelUsed ?? requestedModel, CancellationToken.None);
        var envelope = new AgentRunEnvelopeMetadata
        {
            InvocationId = state.InvocationId,
            DurationMs = durationMs,
            FailureCategory = state.FailureCategory?.ToString(),
            ContentChunkCount = state.StreamedChunkCount,
            ReasoningChunkCount = state.StreamedThinkingChunkCount,
            TraceId = CurrentTraceId(),
            StartedAtUtc = state.StartedAt == default ? null : state.StartedAt.ToUnixTimeMilliseconds(),
            Provider = provider,
            ToolSchemaTokens = state.ToolSchemaTokens,
            MaxToolSchemaTokens = state.MaxToolSchemaTokens,
            DispatchedTier = state.DispatchedTier,
            AuthoredEffort = state.AuthoredEffort,
            ModelReadinessMs = state.ModelReadinessMs,
            // The turn TOTALS ride the envelope; the message row below keeps the last round's counts, which is what the
            // chat context meter reads as occupancy. Two different questions, two different rows.
            TurnInputTokens = state.TurnInputTokens,
            TurnOutputTokens = state.TurnOutputTokens,
            TurnTotalTokens = state.TurnTotalTokens,
            TurnReasoningTokens = state.TurnReasoningTokens
        };

        // A cancelled turn persists NO error text: a user cancel or an operator eject is an outcome, not a failure, so
        // it leaves no red banner. Only the user-facing Error is cleared; the envelope's FailureCategory is untouched.
        var terminalError = terminalStatus == NodeChatMessageStatusValues.Cancelled ? null : state.Error;

        var persisted = await _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
            {
                Correlation = correlation,
                Status = terminalStatus,
                UpdatedAtUtc = NowUnixMilliseconds(),
                Content = state.StreamedContent,
                Reasoning = string.IsNullOrEmpty(state.StreamedThinkingContent) ? null : state.StreamedThinkingContent,
                Error = terminalError,
                Model = state.ModelUsed ?? requestedModel,
                InputCount = state.InputTokens,
                OutputCount = state.OutputTokens,
                TotalCount = state.TotalTokens,
                ReasoningCount = state.ReasoningTokens,
                // Null when the caller assembled no interleave (platform path, or a turn with no parts); the persisted
                // parts are then left untouched. The local front doors pass the accumulated ordered parts here.
                Parts = parts,
                // Whole-turn wall-clock duration from the runner; null for legacy/platform turns that did not report it.
                GenerationDurationMs = state.GenerationDurationMs,
                Envelope = envelope,
                // KB sources that grounded this turn; null when the turn used no knowledge base, which
                // preserves any existing persisted sources on the row.
                Sources = sources
            },
            CancellationToken.None);

        // The transition guard may have rejected this terminalize, so the persisted row is the authoritative winning
        // state and both the returned status and the single SSE terminal are built from it.
        var winningStatus = persisted.Status;

        return new NodeChatPumpTerminalResult
        {
            Persisted = persisted,
            TerminalStatus = winningStatus,
            EventType = MapTerminalEventType(winningStatus, eventType)
        };
    }

    /// <summary>
    ///     Terminalizes a stream that ended WITHOUT a terminal invocation state — interrupted (process/stream loss)
    ///     or cancelled (client cancellation). Writes the last-seen content under the chosen terminal status.
    /// </summary>
    public async Task<NodeChatPumpTerminalResult> TerminalizeInterruptedAsync(NodeChatMessageCorrelation correlation,
        NodeChatPumpCursor cursor,
        bool wasCancelled)
    {
        var terminalStatus = wasCancelled
            ? NodeChatMessageStatusValues.Cancelled
            : NodeChatMessageStatusValues.Interrupted;
        var eventType = wasCancelled
            ? ChatStreamEventTypes.AssistantCancelled
            : ChatStreamEventTypes.AssistantInterrupted;

        // Durable run ledger: a stream that ended without a terminal state still gets one envelope row, written
        // atomically with the terminal message row. It is thin — no InvocationState exists here — but carries the status.
        var envelope = new AgentRunEnvelopeMetadata
        {
            InvocationId = null,
            DurationMs = 0L,
            TraceId = CurrentTraceId()
        };

        // A user cancel persists NO error text; an interrupted stream (process/stream
        // loss) records the interrupted marker so the row is distinguishable on reload. Failures never reach this path.
        var interruptedError = wasCancelled ? null : terminalStatus;

        var persisted = await _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
            {
                Correlation = correlation,
                Status = terminalStatus,
                UpdatedAtUtc = NowUnixMilliseconds(),
                Content = cursor.Content,
                Reasoning = string.IsNullOrEmpty(cursor.Reasoning) ? null : cursor.Reasoning,
                Error = interruptedError,
                Envelope = envelope
            },
            CancellationToken.None);

        // The persisted row is the winning state: the guard may have rejected an Interrupted write against an already
        // terminal row (or a Cancelled write is idempotent over an HTTP-cancelled row). Build the result from it.
        var winningStatus = persisted.Status;

        return new NodeChatPumpTerminalResult
        {
            Persisted = persisted,
            TerminalStatus = winningStatus,
            EventType = MapTerminalEventType(winningStatus, eventType)
        };
    }

    /// <summary>Whether a state represents a terminal invocation outcome the pump should terminalize on.</summary>
    public static bool IsTerminal(InvocationStatus status)
    {
        return status is InvocationStatus.Completed or InvocationStatus.Cancelled or InvocationStatus.Failed;
    }

    private static bool TryMapTerminal(InvocationStatus status, out string terminalStatus, out string eventType)
    {
        switch (status)
        {
            case InvocationStatus.Completed:
                terminalStatus = NodeChatMessageStatusValues.Completed;
                eventType = ChatStreamEventTypes.AssistantCompleted;
                return true;
            case InvocationStatus.Cancelled:
                terminalStatus = NodeChatMessageStatusValues.Cancelled;
                eventType = ChatStreamEventTypes.AssistantCancelled;
                return true;
            case InvocationStatus.Failed:
                terminalStatus = NodeChatMessageStatusValues.Failed;
                eventType = ChatStreamEventTypes.AssistantFailed;
                return true;
            default:
                terminalStatus = string.Empty;
                eventType = string.Empty;
                return false;
        }
    }

    // Maps a persisted terminal MESSAGE status back to its stream event type, so the emitted SSE terminal reflects the
    // row that won. Any unexpected non-terminal status, which this path cannot produce, keeps the requested event.
    private static string MapTerminalEventType(string terminalStatus, string requestedEventType)
    {
        return terminalStatus switch
        {
            NodeChatMessageStatusValues.Completed => ChatStreamEventTypes.AssistantCompleted,
            NodeChatMessageStatusValues.Cancelled => ChatStreamEventTypes.AssistantCancelled,
            NodeChatMessageStatusValues.Failed => ChatStreamEventTypes.AssistantFailed,
            NodeChatMessageStatusValues.Interrupted => ChatStreamEventTypes.AssistantInterrupted,
            _ => requestedEventType
        };
    }

    private long NowUnixMilliseconds()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }

    // W3C trace id of the ambient activity at terminalization (for cross-correlation with exported traces), or null when
    // no activity is in scope. A default (all-zero) id is treated as absent.
    private static string? CurrentTraceId()
    {
        if (Activity.Current is not { } activity)
        {
            return null;
        }

        var traceId = activity.TraceId;
        return traceId == default ? null : traceId.ToString();
    }
}
