namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Shared per-invocation persistence pump. It consumes the <see cref="InvocationState" /> deltas a
///     single agent run produces and persists them to node SQLite through <see cref="INodeChatPersistenceService" />
///     — flushing streamed partials and terminalizing the assistant message — for BOTH front doors:
///     <list type="bullet">
///         <item>
///             local loopback (<see cref="NodeChatStreamService" />), which additionally turns each persisted state
///             into a <see cref="ChatStreamEvent" /> for its SSE response;
///         </item>
///         <item>the platform path (<c>WorkerEventDispatcher</c>), which only needs the persistence side.</item>
///     </list>
///     The pump owns no agent logic and no transport: it is driven one <see cref="InvocationState" /> at a time by
///     the caller (the caller decides where states come from — a local channel or the dispatcher's
///     <c>InvocationStateChanged</c> stream). It is the write counterpart to the read-only
///     <see cref="InvocationResumeRegistry" />, which translates the same states into resume events.
/// </summary>
public sealed class NodeChatInvocationPump(
    INodeChatPersistenceService persistence,
    IUsageProviderResolver usageProviderResolver,
    TimeProvider timeProvider) : INodeChatInvocationPump
{
    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    private readonly IUsageProviderResolver _usageProviderResolver = usageProviderResolver ?? throw new ArgumentNullException(nameof(usageProviderResolver));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    ///     Persists a streamed content/reasoning delta if the incoming state has advanced past
    ///     <paramref name="cursor" />. Returns the updated cursor and, when a delta was persisted, the persisted
    ///     message plus the raw delta slices so the caller can emit a stream event. When nothing advanced the
    ///     returned <see cref="NodeChatPumpFlushResult.Persisted" /> is null and the cursor is unchanged.
    /// </summary>
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
            return new NodeChatPumpFlushResult(cursor, Persisted: null, ContentDelta: null, ReasoningDelta: null);
        }

        var contentDelta = hasContentDelta ? state.StreamedContent[cursor.Content.Length..] : null;
        var reasoningDelta = hasReasoningDelta ? state.StreamedThinkingContent[cursor.Reasoning.Length..] : null;
        var nextCursor = new NodeChatPumpCursor(state.StreamedContent, state.StreamedThinkingContent);

        var persisted = await _persistence.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation,
                nextCursor.Content,
                string.IsNullOrEmpty(nextCursor.Reasoning) ? null : nextCursor.Reasoning,
                NowUnixMilliseconds()),
            cancellationToken).ConfigureAwait(false);

        return new NodeChatPumpFlushResult(nextCursor, persisted, contentDelta, reasoningDelta);
    }

    /// <summary>
    ///     Terminalizes the assistant message from a terminal <see cref="InvocationState" /> (Completed / Cancelled /
    ///     Failed). Always persists on <see cref="CancellationToken.None" /> so the terminal row is written even when
    ///     the run was cancelled. Returns the persisted message and the resolved terminal status/event type.
    /// </summary>
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

        // Durable run ledger: the envelope payload rides INTO the terminalize command so its content-free
        // row is written in the SAME transaction as the terminal message row (both commit or roll back together — no
        // swallowed best-effort write). The terminal status/success and the bound agent id are derived from the winning
        // persisted row inside that transaction, so they are not carried here.
        var durationMs = state.GenerationDurationMs
                         ?? (state.CompletedAt is { } completedAt ? Math.Max(val1: 0L, (long)(completedAt - state.StartedAt).TotalMilliseconds) : 0L);

        // Attribute the turn's tokens to the fine-grained provider that served it, resolved from the same model id that
        // rides into terminalize (state.ModelUsed ?? requestedModel). Best-effort: the resolver never throws and is bounded,
        // degrading to 'unknown' on any failure/timeout, so provider attribution can never break or stall terminalization.
        var provider = await _usageProviderResolver.ResolveAsync(state.ModelUsed ?? requestedModel, CancellationToken.None).ConfigureAwait(false);
        var envelope = new AgentRunEnvelopeMetadata(state.InvocationId,
            durationMs,
            state.FailureCategory?.ToString(),
            state.StreamedChunkCount,
            state.StreamedThinkingChunkCount,
            CurrentTraceId(),
            state.StartedAt == default ? null : state.StartedAt.ToUnixTimeMilliseconds(),
            provider,
            state.ToolSchemaTokens,
            state.MaxToolSchemaTokens,
            state.DispatchedTier,
            state.AuthoredEffort,
            state.ModelReadinessMs);

        // A cancelled turn persists NO error text — a user cancel (or an operator eject,
        // also Cancelled-category) is an outcome, not a failure, so it must not leave a red error banner on the row.
        // Failures keep their classified message. Derived from the winning terminal status the state maps to, so the
        // envelope's FailureCategory (a content-free ledger field) is untouched — only the user-facing Error is cleared.
        var terminalError = terminalStatus == NodeChatMessageStatusValues.Cancelled ? null : state.Error;

        var persisted = await _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                terminalStatus,
                NowUnixMilliseconds(),
                state.StreamedContent,
                string.IsNullOrEmpty(state.StreamedThinkingContent) ? null : state.StreamedThinkingContent,
                terminalError,
                state.ModelUsed ?? requestedModel,
                state.InputTokens,
                state.OutputTokens,
                state.TotalTokens,
                state.ReasoningTokens,
                // Null when the caller assembled no interleave (platform path, or a turn with no parts); the persisted
                // parts are then left untouched. The local front doors pass the accumulated ordered parts here.
                parts,
                // Whole-turn wall-clock duration from the runner; null for legacy/platform turns that did not report it.
                state.GenerationDurationMs,
                envelope,
                // KB sources that grounded this turn; null when the turn used no knowledge base, which
                // preserves any existing persisted sources on the row.
                sources),
            CancellationToken.None).ConfigureAwait(false);

        // The transition guard may have rejected this terminalize (the row already reached a different terminal), so the
        // persisted row is the authoritative winning state. The returned status and the single SSE terminal are built from
        // it rather than the requested terminal.
        var winningStatus = persisted.Status;

        return new NodeChatPumpTerminalResult(persisted, winningStatus, MapTerminalEventType(winningStatus, eventType));
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

        // Durable run ledger: a stream that ended without a terminal invocation state still gets one
        // envelope row, written atomically with the terminal message row. Thinner than the state-driven path — there is no
        // InvocationState here, so invocation id / tokens / duration / chunk counts are unknown and omitted; the terminal
        // status (derived from the winning row) carries the interrupted/cancelled outcome.
        var envelope = new AgentRunEnvelopeMetadata(InvocationId: null, DurationMs: 0L, TraceId: CurrentTraceId());

        // A user cancel persists NO error text; an interrupted stream (process/stream
        // loss) records the interrupted marker so the row is distinguishable on reload. Failures never reach this path.
        var interruptedError = wasCancelled ? null : terminalStatus;

        var persisted = await _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                terminalStatus,
                NowUnixMilliseconds(),
                cursor.Content,
                string.IsNullOrEmpty(cursor.Reasoning) ? null : cursor.Reasoning,
                interruptedError,
                Envelope: envelope),
            CancellationToken.None).ConfigureAwait(false);

        // The persisted row is the winning state: the guard may have rejected an Interrupted write against an already
        // terminal row (or a Cancelled write is idempotent over an HTTP-cancelled row). Build the result from it.
        var winningStatus = persisted.Status;

        return new NodeChatPumpTerminalResult(persisted, winningStatus, MapTerminalEventType(winningStatus, eventType));
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
    // row that actually won rather than the requested terminal. Falls back to the requested event for any unexpected
    // (non-terminal) status, which the terminalize path cannot produce.
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
