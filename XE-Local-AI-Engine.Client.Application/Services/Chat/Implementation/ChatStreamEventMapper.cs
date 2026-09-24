namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Globalization;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     The single source of truth for turning persisted messages and tool-call lifecycle payloads into
///     <see cref="ChatStreamEvent" />s.
/// </summary>
/// <remarks>
///     The send, regenerate and resume paths all map through here, so a live stream and a resumed one can never drift
///     in wire shape — event type, field placement, phase-gated tool fields. Identity fields are passed explicitly
///     because the sources differ, a correlation live and the invocation id on resume; everything else maps alike.
/// </remarks>
internal static class ChatStreamEventMapper
{
    // Web defaults => camelCase property names, matching every other JSON payload the chat stream carries (tool
    // Arguments, the persisted parts[]), so the client parses one convention.
    private static readonly JsonSerializerOptions QuestionsJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Maps a persisted terminal message status to its stream event type.
    /// </summary>
    /// <remarks>
    ///     Used when a lifecycle mark is rejected because a cancel raced ahead of the run and the row is already
    ///     terminal: the caller emits this event instead of the queued or streaming one and aborts.
    /// </remarks>
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
    ///     Maps a persisted row to a LIFECYCLE or TERMINAL event, and to the user's own persisted message.
    /// </summary>
    /// <remarks>
    ///     These carry the row's full <see cref="ChatStreamEvent.Content" /> and
    ///     <see cref="ChatStreamEvent.Reasoning" />, affordable because there is at most one per turn. It deliberately
    ///     cannot build an <see cref="ChatStreamEventTypes.AssistantDelta" />: a delta sourced from a persisted row
    ///     makes the wire cost of a turn quadratic in its output length. Deltas go through <see cref="DeltaEvent" />,
    ///     which has no access to a row at all, so the type system answers which fields an event carries.
    /// </remarks>
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
        return new ChatStreamEvent
        {
            Type = type,
            ConversationId = correlation.ConversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            Status = message.Status,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            Content = message.Content,
            Reasoning = message.Reasoning,
            Error = message.Error,
            Model = message.Model,
            InputTokens = inputTokens ?? message.InputCount,
            OutputTokens = outputTokens ?? message.OutputCount,
            TotalTokens = totalTokens ?? message.TotalCount,
            ReasoningTokens = reasoningTokens ?? message.ReasoningCount,
            InvocationTimeoutSeconds = invocationTimeoutSeconds
        };
    }

    /// <summary>
    ///     Builds one live <see cref="ChatStreamEventTypes.AssistantDelta" />: the content and reasoning increment plus
    ///     the character offset each begins at, and NOTHING else.
    /// </summary>
    /// <remarks>
    ///     It takes no <see cref="NodeChatPersistedMessageDto" />, so a delta frame needs no database row, which is
    ///     what lets the SSE cadence run far ahead of the slower, growth-triggered flush. The offsets are the client's
    ///     gap detector: it appends at the offset it expected and re-subscribes, receiving an
    ///     <see cref="ChatStreamEventTypes.AssistantSnapshot" />, if they do not line up.
    /// </remarks>
    public static ChatStreamEvent DeltaEvent(NodeChatMessageCorrelation correlation,
        long timestampMs,
        long sequence,
        string? contentDelta,
        string? reasoningDelta,
        long contentOffset,
        long reasoningOffset)
    {
        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.AssistantDelta,
            ConversationId = correlation.ConversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            Delta = contentDelta,
            ReasoningDelta = reasoningDelta,
            ContentOffset = contentOffset,
            ReasoningOffset = reasoningOffset
        };
    }

    /// <summary>
    ///     Builds an <see cref="ChatStreamEventTypes.AssistantSnapshot" />: an authoritative replacement of the
    ///     client's accumulated text, with the offsets the next delta continues from.
    /// </summary>
    /// <remarks>
    ///     Used by the resume replay and by the repair paths a client reaches through <c>ResumeMessage</c> after a gap
    ///     or a queue overflow. Its status stays <c>streaming</c>, because a snapshot is a mid-stream replacement and
    ///     never a terminal. The resume path stamps the invocation id as BOTH the message id and the request id, so
    ///     the ids are passed explicitly rather than as a correlation.
    /// </remarks>
    /// <param name="model">The model the invocation runs on, known from its runtime package before the first token.</param>
    public static ChatStreamEvent SnapshotEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        string content,
        string? reasoning,
        long timestampMs,
        long sequence,
        string? model = null)
    {
        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.AssistantSnapshot,
            Model = model,
            ConversationId = conversationId,
            MessageId = messageId,
            RequestId = requestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            Content = content,
            Reasoning = reasoning,
            ContentOffset = content?.Length ?? 0,
            ReasoningOffset = reasoning?.Length ?? 0
        };
    }

    /// <summary>
    ///     Builds an <see cref="ChatStreamEventTypes.AssistantReconcile" />, telling the client that this stream is no
    ///     longer contiguous and it must resynchronize.
    /// </summary>
    /// <remarks>
    ///     It carries no payload beyond the correlation and a sequence, because the repair carries the state: the
    ///     client re-subscribes through <c>ResumeMessage</c> and its first frame is an authoritative
    ///     <see cref="SnapshotEvent" />. It is raised when a bounded stream queue overflowed or when a resume replay
    ///     snapshot was too large to send. The client surfaces nothing to the user;
    ///     <c>chat_stream_reconcile_total</c> is the signal that it is happening.
    /// </remarks>
    public static ChatStreamEvent ReconcileEvent(NodeChatMessageCorrelation correlation,
        long timestampMs,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(correlation);

        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.AssistantReconcile,
            ConversationId = correlation.ConversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs
        };
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

        return new ChatStreamEvent
        {
            Type = type,
            ConversationId = conversationId,
            MessageId = messageId,
            RequestId = requestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            ToolCallId = payload.ToolCallId,
            ToolName = payload.ToolName,
            Arguments = payload.Phase == ToolCallLifecyclePhase.Requested ? payload.Arguments : null,
            RequiresApproval = payload.Phase == ToolCallLifecyclePhase.Requested ? payload.RequiresApproval : null,
            Result = payload.Phase == ToolCallLifecyclePhase.Completed ? payload.Result : null,
            IsError = payload.Phase == ToolCallLifecyclePhase.Completed ? payload.IsError : null
        };
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
        long sequence,
        // When the phase CHANGED, off InvocationState. A different clock from timestampMs, the frame's send time off
        // the injected TimeProvider: under a test clock the two disagree by decades, and nothing may relate them.
        DateTimeOffset? phaseChangedAtUtc = null)
    {
        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.AssistantPhase,
            ConversationId = correlation.ConversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            RuntimePhase = ToWirePhase(phase),
            RuntimePhaseChangedAtUtc = phaseChangedAtUtc?.ToString("O", CultureInfo.InvariantCulture)
        };
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
        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.AssistantNotice,
            ConversationId = conversationId,
            MessageId = messageId,
            RequestId = requestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            NoticeKind = payload.Kind.ToString(),
            NoticeMessage = payload.Message,
            NoticeDetail = payload.Detail
        };
    }

    public static void AccumulateNotice(NodeChatPartAccumulator parts, TurnNoticePayload payload, long sequence)
    {
        parts.AppendNotice(payload.Kind.ToString(), payload.Message, sequence, payload.Detail);
    }

    /// <summary>
    ///     Maps a pending tool-approval request to an <see cref="ChatStreamEventTypes.ApprovalRequested" /> event.
    /// </summary>
    /// <remarks>
    ///     The tool-call id rides <see cref="ChatStreamEvent.ToolCallId" /> so the client attaches the controls to the
    ///     matching card, and the request id rides <see cref="ChatStreamEvent.ApprovalRequestId" /> for the resolve
    ///     round-trip. It is deliberately NOT accumulated into the persisted <c>parts[]</c>: a reloaded terminal turn
    ///     shows the tool result, never a lingering prompt. A blank call id or tool name maps to a null wire field,
    ///     which tells the client there is no card to attach to; an empty string would look like an unmatchable id.
    /// </remarks>
    public static ChatStreamEvent ApprovalRequestedEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        ApprovalLifecyclePayload payload,
        long timestampMs,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.ApprovalRequested,
            ConversationId = conversationId,
            MessageId = messageId,
            RequestId = requestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            ToolCallId = NullIfBlank(payload.CallId),
            ToolName = NullIfBlank(payload.ToolName),
            Arguments = payload.Arguments,
            ApprovalRequestId = payload.RequestId,
            SessionScopeEligible = payload.SessionScopeEligible
        };
    }

    /// <summary>
    ///     Maps a pending <c>ask_user</c> question to a <see cref="ChatStreamEventTypes.QuestionRequested" /> event.
    /// </summary>
    /// <remarks>
    ///     Shaped exactly like <see cref="ApprovalRequestedEvent" />, with the questions themselves serialized into
    ///     <see cref="ChatStreamEvent.Questions" />, because a client cannot render an answerable prompt from a
    ///     correlation id alone. It is NOT accumulated into the persisted <c>parts[]</c> for the same reason: a
    ///     reloaded terminal turn shows the tool result rather than a lingering form. A still-PENDING question
    ///     survives a reconnect through <c>InvocationState.PendingQuestion</c> instead.
    /// </remarks>
    public static ChatStreamEvent QuestionRequestedEvent(Guid conversationId,
        Guid messageId,
        Guid requestId,
        UserQuestionLifecyclePayload payload,
        long timestampMs,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return new ChatStreamEvent
        {
            Type = ChatStreamEventTypes.QuestionRequested,
            ConversationId = conversationId,
            MessageId = messageId,
            RequestId = requestId,
            Status = NodeChatMessageStatusValues.Streaming,
            Sequence = sequence,
            OccurredAtUtc = timestampMs,
            ToolCallId = NullIfBlank(payload.CallId),
            ToolName = NullIfBlank(payload.ToolName),
            QuestionRequestId = payload.RequestId,
            Questions = JsonSerializer.Serialize(payload.Questions, QuestionsJsonOptions)
        };
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
