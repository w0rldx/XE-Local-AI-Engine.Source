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

    public static ChatStreamEvent MessageEvent(string type,
        NodeChatMessageCorrelation correlation,
        NodeChatPersistedMessageDto message,
        long timestampMs,
        long sequence,
        string? delta = null,
        string? reasoningDelta = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        int? reasoningTokens = null)
    {
        return new ChatStreamEvent(type,
            correlation.ConversationId,
            correlation.MessageId,
            correlation.RequestId,
            message.Status,
            sequence,
            timestampMs,
            delta,
            reasoningDelta,
            message.Content,
            message.Reasoning,
            message.Error,
            message.Model,
            inputTokens ?? message.InputCount,
            outputTokens ?? message.OutputCount,
            totalTokens ?? message.TotalCount,
            reasoningTokens ?? message.ReasoningCount);
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
            NoticeMessage: payload.Message);
    }

    public static void AccumulateNotice(NodeChatPartAccumulator parts, TurnNoticePayload payload, long sequence)
    {
        parts.AppendNotice(payload.Kind.ToString(), payload.Message, sequence);
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
            ApprovalRequestId: payload.RequestId);
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
