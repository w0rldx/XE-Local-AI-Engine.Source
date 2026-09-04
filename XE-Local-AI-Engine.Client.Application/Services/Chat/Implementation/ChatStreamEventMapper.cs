namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     The single source of truth for turning persisted messages and tool-call lifecycle payloads into
///     <see cref="ChatStreamEvent" />s. The local send path (<see cref="NodeChatStreamService" />), the regenerate
///     path (<see cref="NodeChatRegenerationService" />), and the reconnect/resume path
///     (<see cref="InvocationResumeRegistry" />) all map through here so a live stream and a resumed stream can never
///     drift in wire shape (event type, field placement, phase-gated tool fields). Identity fields are passed in
///     explicitly because the sources differ (a correlation on the live paths, the invocation id on the resume path);
///     the mapping of every other field is identical.
/// </summary>
internal static class ChatStreamEventMapper
{
    // Web defaults => camelCase property names, matching every other JSON payload the chat stream carries (tool
    // Arguments, the persisted parts[]), so the client parses one convention.
    private static readonly JsonSerializerOptions QuestionsJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Maps a persisted terminal message status to its stream event type. Used when a lifecycle mark (queued /
    ///     streaming) is rejected because the row already reached a terminal status (a cancel raced ahead of the run):
    ///     the caller emits this terminal event instead of the queued/streaming event and aborts.
    /// </summary>
    public static string TerminalEventType(string status)
    {
        return status switch
        {
            NodeChatMessageStatusValues.Completed => ChatStreamEventTypes.AssistantCompleted,
            NodeChatMessageStatusValues.Cancelled => ChatStreamEventTypes.AssistantCancelled,
            NodeChatMessageStatusValues.Failed => ChatStreamEventTypes.AssistantFailed,
            _ => ChatStreamEventTypes.AssistantInterrupted
        };
    }

    /// <summary>
    ///     Maps a persisted row to a LIFECYCLE or TERMINAL event — pending / queued / streaming / completed / cancelled
    ///     / failed / interrupted, and the user's own persisted message. These carry the full
    ///     <see cref="ChatStreamEvent.Content" />/<see cref="ChatStreamEvent.Reasoning" /> from the row, which is
    ///     affordable because there is at most one of them per turn.
    ///     <para>
    ///         It deliberately cannot build an <see cref="ChatStreamEventTypes.AssistantDelta" />: the delta path took
    ///         its content from the persisted row too, which is what made the wire cost of a turn quadratic in its
    ///         output length. Deltas go through <see cref="DeltaEvent" />, which has no access to a row at all — the
    ///         type system now answers "which fields does this event carry", instead of a caller's discipline.
    ///     </para>
    /// </summary>
    public static ChatStreamEvent MessageEvent(string type,
        NodeChatMessageCorrelation correlation,
        NodeChatPersistedMessageDto message,
        long timestampMs,
        long sequence,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        int? reasoningTokens = null,
        int? invocationTimeoutSeconds = null)
    {
        return new ChatStreamEvent(type,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            message.Status,
            sequence,
            timestampMs,
            Content: message.Content,
            Reasoning: message.Reasoning,
            Error: message.Error,
            Model: message.Model,
            InputTokens: inputTokens ?? message.InputCount,
            OutputTokens: outputTokens ?? message.OutputCount,
            TotalTokens: totalTokens ?? message.TotalCount,
            ReasoningTokens: reasoningTokens ?? message.ReasoningCount,
            InvocationTimeoutSeconds: invocationTimeoutSeconds);
    }

    /// <summary>
    ///     Builds one live <see cref="ChatStreamEventTypes.AssistantDelta" />: the content/reasoning increment plus the
    ///     character offset each begins at, and NOTHING else — no accumulated content, no model, no token counts.
    ///     <para>
    ///         Note it takes no <see cref="NodeChatPersistedMessageDto" />. A delta frame no longer needs a database row,
    ///         which is what lets the SSE cadence run at ~25 frames/s while persistence flushes on a far slower,
    ///         growth-triggered cadence. The offsets are the client's gap detector: it appends the delta at the offset
    ///         it expected, and re-subscribes (receiving an <see cref="ChatStreamEventTypes.AssistantSnapshot" />) if
    ///         the offsets do not line up.
    ///     </para>
    /// </summary>
    public static ChatStreamEvent DeltaEvent(NodeChatMessageCorrelation correlation,
        long timestampMs,
        long sequence,
        string? contentDelta,
        string? reasoningDelta,
        long contentOffset,
        long reasoningOffset)
    {
        return new ChatStreamEvent(ChatStreamEventTypes.AssistantDelta,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs,
            contentDelta,
            reasoningDelta,
            ContentOffset: contentOffset,
            ReasoningOffset: reasoningOffset);
    }

    /// <summary>
    ///     Builds an <see cref="ChatStreamEventTypes.AssistantSnapshot" />: an authoritative replacement of the
    ///     client's accumulated text, with the offsets the next delta continues from. Used by the resume replay, and by
    ///     the repair paths a client reaches through <c>ResumeMessage</c> after a gap or a queue overflow.
    ///     <para>
    ///         Its status stays <c>streaming</c> — a snapshot is a mid-stream state replacement, never a terminal. The
    ///         resume path stamps the invocation id as BOTH the message id and the request id, as it does for tool-call
    ///         and notice replay, so the ids are passed explicitly rather than as a correlation.
    ///     </para>
    /// </summary>
    public static ChatStreamEvent SnapshotEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        string content,
        string? reasoning,
        long timestampMs,
        long sequence)
    {
        return new ChatStreamEvent(ChatStreamEventTypes.AssistantSnapshot,
            conversationId,
            messageId,
            requestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs,
            Content: content,
            Reasoning: reasoning,
            ContentOffset: content?.Length ?? 0,
            ReasoningOffset: reasoning?.Length ?? 0);
    }

    /// <summary>
    ///     Builds an <see cref="ChatStreamEventTypes.AssistantReconcile" />: "this stream is no longer contiguous —
    ///     resynchronize". Carries no payload beyond the correlation and a sequence, because the repair carries the
    ///     state: the client re-subscribes through <c>ResumeMessage</c> and its first frame is an authoritative
    ///     <see cref="SnapshotEvent" />.
    ///     <para>
    ///         Raised when a bounded stream queue overflowed (<see cref="ChatStreamEventSink" />) or when a resume
    ///         replay snapshot was too large to send (<see cref="InvocationResumeRegistry" />). The client consumes it
    ///         in the adapter and surfaces nothing to the user; <c>chat_stream_reconcile_total</c> is the signal that
    ///         it is happening.
    ///     </para>
    /// </summary>
    public static ChatStreamEvent ReconcileEvent(NodeChatMessageCorrelation correlation,
        long timestampMs,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        return new ChatStreamEvent(ChatStreamEventTypes.AssistantReconcile,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs);
    }

    public static ChatStreamEvent ToolCallEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        ToolCallLifecyclePayload payload,
        long timestampMs,
        long sequence)
    {
        var type = payload.Phase == ToolCallLifecyclePhase.Requested
            ? ChatStreamEventTypes.ToolCallRequested
            : ChatStreamEventTypes.ToolCallCompleted;

        return new ChatStreamEvent(type,
            conversationId,
            messageId,
            requestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs,
            ToolCallId: payload.ToolCallId,
            ToolName: payload.ToolName,
            Arguments: payload.Phase == ToolCallLifecyclePhase.Requested ? payload.Arguments : null,
            RequiresApproval: payload.Phase == ToolCallLifecyclePhase.Requested ? payload.RequiresApproval : null,
            Result: payload.Phase == ToolCallLifecyclePhase.Completed ? payload.Result : null,
            IsError: payload.Phase == ToolCallLifecyclePhase.Completed ? payload.IsError : null);
    }

    public static void AccumulateToolPart(NodeChatPartAccumulator parts, ToolCallLifecyclePayload payload, long sequence)
    {
        if (payload.Phase == ToolCallLifecyclePhase.Requested)
        {
            parts.AppendToolRequested(payload.ToolCallId, payload.ToolName, payload.Arguments, payload.RequiresApproval, sequence);
            return;
        }

        parts.CompleteToolCall(payload.ToolCallId, payload.ToolName, payload.Result, payload.IsError, sequence);
    }

    /// <summary>
    ///     Maps a pre-first-token runtime-phase transition to a content-free <see cref="ChatStreamEventTypes.AssistantPhase" />
    ///     event so the client can render a distinct "Loading model…" indicator during a cold load. Carries only the
    ///     wire phase; status stays <c>streaming</c> and no content/tokens ride it.
    /// </summary>
    public static ChatStreamEvent PhaseEvent(NodeChatMessageCorrelation correlation,
        InvocationRuntimePhase phase,
        long timestampMs,
        long sequence)
    {
        return new ChatStreamEvent(ChatStreamEventTypes.AssistantPhase,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs,
            RuntimePhase: ToWirePhase(phase));
    }

    /// <summary>The wire form of <see cref="InvocationRuntimePhase" /> the React reducer keys the loading indicator on.</summary>
    private static string ToWirePhase(InvocationRuntimePhase phase)
    {
        return phase switch
        {
            InvocationRuntimePhase.PreparingRuntime => "preparing_runtime",
            InvocationRuntimePhase.LoadingModel => "loading_model",
            _ => "generating"
        };
    }

    public static ChatStreamEvent NoticeEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        TurnNoticePayload payload,
        long timestampMs,
        long sequence)
    {
        return new ChatStreamEvent(ChatStreamEventTypes.AssistantNotice,
            conversationId,
            messageId,
            requestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs,
            NoticeKind: payload.Kind.ToString(),
            NoticeMessage: payload.Message,
            NoticeDetail: payload.Detail);
    }

    public static void AccumulateNotice(NodeChatPartAccumulator parts, TurnNoticePayload payload, long sequence)
    {
        parts.AppendNotice(payload.Kind.ToString(), payload.Message, sequence, payload.Detail);
    }

    /// <summary>
    ///     Maps a pending tool-approval request to an <see cref="ChatStreamEventTypes.ApprovalRequested" /> stream event.
    ///     The tool-call id rides <see cref="ChatStreamEvent.ToolCallId" /> so the client attaches the
    ///     Approve/Deny controls to the matching tool-call card; the approval request id rides
    ///     <see cref="ChatStreamEvent.ApprovalRequestId" /> for the resolve round-trip. Deliberately NOT accumulated into
    ///     the persisted <c>parts[]</c>: the pending approval is transient live state, and a reloaded terminal turn shows
    ///     the executed/rejected tool result, never a lingering approval prompt.
    ///     <para>
    ///         A blank call id / tool name maps to a null wire field rather than an empty string. The live path always
    ///         populates both; the reconnect replay rebuilds the payload from <c>InvocationApprovalState</c>, whose
    ///         CallId/ToolName are optional (a platform-hub approval carries only an id and a description). A null tells
    ///         the client "no card to attach this to" — an empty string would look like a real, unmatchable id.
    ///     </para>
    /// </summary>
    public static ChatStreamEvent ApprovalRequestedEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        ApprovalLifecyclePayload payload,
        long timestampMs,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new ChatStreamEvent(ChatStreamEventTypes.ApprovalRequested,
            conversationId,
            messageId,
            requestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs,
            ToolCallId: NullIfBlank(payload.CallId),
            ToolName: NullIfBlank(payload.ToolName),
            ApprovalRequestId: payload.RequestId,
            SessionScopeEligible: payload.SessionScopeEligible);
    }

    /// <summary>
    ///     Maps a pending <c>ask_user</c> question to a <see cref="ChatStreamEventTypes.QuestionRequested" /> stream
    ///     event. Shaped exactly like <see cref="ApprovalRequestedEvent" /> — the tool-call id rides
    ///     <see cref="ChatStreamEvent.ToolCallId" /> so the client attaches the question card to the matching tool-call
    ///     card, and the request id rides <see cref="ChatStreamEvent.QuestionRequestId" /> for the resolve round-trip —
    ///     with the questions themselves serialized into <see cref="ChatStreamEvent.Questions" />, because a client
    ///     cannot render an answerable prompt from a correlation id alone.
    ///     <para>
    ///         Deliberately NOT accumulated into the persisted <c>parts[]</c>, for the same reason the approval event is
    ///         not: the prompt is transient live state that the resolve endpoint clears, and a reloaded terminal turn
    ///         shows the tool result (the operator's answer, or the not-answered sentinel) rather than a lingering form.
    ///         A still-PENDING question survives a reconnect through <c>InvocationState.PendingQuestion</c> instead —
    ///         see <see cref="InvocationResumeRegistry" />.
    ///     </para>
    /// </summary>
    public static ChatStreamEvent QuestionRequestedEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        UserQuestionLifecyclePayload payload,
        long timestampMs,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new ChatStreamEvent(ChatStreamEventTypes.QuestionRequested,
            conversationId,
            messageId,
            requestId,
            NodeChatMessageStatusValues.Streaming,
            sequence,
            timestampMs,
            ToolCallId: NullIfBlank(payload.CallId),
            ToolName: NullIfBlank(payload.ToolName),
            QuestionRequestId: payload.RequestId,
            Questions: JsonSerializer.Serialize(payload.Questions, QuestionsJsonOptions));
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
