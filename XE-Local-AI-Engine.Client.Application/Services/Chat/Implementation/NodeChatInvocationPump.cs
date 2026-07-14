namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
    TimeProvider timeProvider,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<NodeChatInvocationPump> logger) : INodeChatInvocationPump
{
    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    // The pump is a singleton but the run-envelope store (IAgentExecutionLogStore) is scoped over the per-scope
    // NodeChatDbContext, so each ledger write gets its own DI scope (mirrors the memory extraction worker).
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
    private readonly ILogger<NodeChatInvocationPump> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        IReadOnlyList<NodeChatMessagePart>? parts = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!TryMapTerminal(state.Status, out var terminalStatus, out var eventType))
        {
            throw new ArgumentException($"Invocation status '{state.Status}' is not terminal.", nameof(state));
        }

        var persisted = await _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                terminalStatus,
                NowUnixMilliseconds(),
                state.StreamedContent,
                string.IsNullOrEmpty(state.StreamedThinkingContent) ? null : state.StreamedThinkingContent,
                state.Error,
                state.ModelUsed ?? requestedModel,
                state.InputTokens,
                state.OutputTokens,
                state.TotalTokens,
                state.ReasoningTokens,
                // Null when the caller assembled no interleave (platform path, or a turn with no parts); the persisted
                // parts are then left untouched. The local front doors pass the accumulated ordered parts here.
                parts,
                // Whole-turn wall-clock duration from the runner; null for legacy/platform turns that did not report it.
                state.GenerationDurationMs),
            CancellationToken.None).ConfigureAwait(false);

        // Durable run ledger (MED-007): one content-free envelope row per terminalized invocation. Best-effort — a
        // ledger failure is swallowed inside the helper so it can never fail or corrupt the terminalized turn.
        var durationMs = state.GenerationDurationMs
                         ?? (state.CompletedAt is { } completedAt ? Math.Max(val1: 0L, (long)(completedAt - state.StartedAt).TotalMilliseconds) : 0L);
        await WriteRunEnvelopeAsync(new AgentRunEnvelopeInput(correlation.ConversationId,
                correlation.MessageId,
                state.InvocationId,
                correlation.RequestId,
                state.ModelUsed ?? requestedModel ?? string.Empty,
                terminalStatus,
                state.Status == InvocationStatus.Completed,
                durationMs,
                state.FailureCategory?.ToString(),
                state.InputTokens,
                state.OutputTokens,
                state.StreamedChunkCount,
                state.StreamedThinkingChunkCount,
                CurrentTraceId()))
            .ConfigureAwait(false);

        return new NodeChatPumpTerminalResult(persisted, terminalStatus, eventType);
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

        var persisted = await _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                terminalStatus,
                NowUnixMilliseconds(),
                cursor.Content,
                string.IsNullOrEmpty(cursor.Reasoning) ? null : cursor.Reasoning,
                terminalStatus),
            CancellationToken.None).ConfigureAwait(false);

        // Durable run ledger (MED-007): a stream that ended without a terminal invocation state still gets one envelope
        // row. Thinner than the state-driven path — there is no InvocationState here, so invocation id / tokens /
        // duration / chunk counts are unknown and omitted; the terminal status carries the interrupted/cancelled outcome.
        await WriteRunEnvelopeAsync(new AgentRunEnvelopeInput(correlation.ConversationId,
                correlation.MessageId,
                InvocationId: null,
                correlation.RequestId,
                ModelName: string.Empty,
                terminalStatus,
                Success: false,
                DurationMs: 0L,
                FailureCategory: null,
                PromptTokens: null,
                CompletionTokens: null,
                ContentChunkCount: null,
                ReasoningChunkCount: null,
                CurrentTraceId()))
            .ConfigureAwait(false);

        return new NodeChatPumpTerminalResult(persisted, terminalStatus, eventType);
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

    private async Task WriteRunEnvelopeAsync(AgentRunEnvelopeInput input)
    {
        try
        {
            await using var scope = _serviceScopeFactory.CreateAsyncScope();
            var executionLogStore = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();
            // Never the send token: the ledger row must be written even when the run was cancelled (mirrors the
            // CancellationToken.None terminalize write above).
            await executionLogStore.AddRunEnvelopeAsync(input, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Best-effort observability: a ledger write failure must never fail or corrupt the invocation. Log the
            // exception TYPE NAME only (the envelope itself carries no content) and continue.
            _logger.LogWarning("Durable run-envelope ledger write failed ({ErrorClass}); the invocation is unaffected.", exception.GetType().Name);
        }
    }
}
