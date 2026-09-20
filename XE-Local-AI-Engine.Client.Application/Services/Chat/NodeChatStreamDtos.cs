namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Represents chat stream event types.
/// </summary>
public static class ChatStreamEventTypes
{
    public const string UserMessagePersisted = "user-message-persisted";
    public const string AssistantPending = "assistant-pending";
    public const string AssistantQueued = "assistant-queued";
    public const string AssistantStreaming = "assistant-streaming";

    /// <summary>
    ///     A pre-first-token runtime-phase transition, carrying only <see cref="ChatStreamEvent.RuntimePhase" /> so
    ///     the client shows "Loading model…" during a cold load. Emitted only while a LOCAL model warms.
    /// </summary>
    public const string AssistantPhase = "assistant-phase";

    /// <summary>
    ///     One live increment of the assistant's content or reasoning, carrying ONLY the deltas plus the character
    ///     offset each begins at, never the accumulated text.
    /// </summary>
    /// <remarks>
    ///     A full snapshot per frame makes the wire cost of a turn quadratic in its output length, so
    ///     <see cref="ChatStreamEvent.Content" /> and <see cref="ChatStreamEvent.Reasoning" /> are never populated
    ///     here and the client appends rather than replaces. It repairs a discontinuity by re-subscribing through
    ///     <c>ResumeMessage</c>, whose first frame is an <see cref="AssistantSnapshot" />.
    /// </remarks>
    public const string AssistantDelta = "assistant-delta";

    /// <summary>
    ///     An authoritative REPLACEMENT of the accumulated text, carrying the offsets the next delta continues from.
    /// </summary>
    /// <remarks>
    ///     It is emitted on resume replay, gap repair and queue overflow, never mid-stream on the happy path, and is
    ///     deliberately not terminal: its status stays <c>streaming</c> and the turn continues after it.
    /// </remarks>
    public const string AssistantSnapshot = "assistant-snapshot";

    /// <summary>
    ///     A "resynchronize" marker: the server could not deliver a contiguous stream, because the consumer's queue
    ///     overflowed or a replay snapshot was too large.
    /// </summary>
    /// <remarks>
    ///     It carries no payload beyond the correlation and a sequence; the client re-subscribes through
    ///     <c>ResumeMessage</c> and consumes it without surfacing anything to the user.
    /// </remarks>
    public const string AssistantReconcile = "assistant-reconcile";

    public const string AssistantCompleted = "assistant-completed";
    public const string AssistantCancelled = "assistant-cancelled";
    public const string AssistantFailed = "assistant-failed";
    public const string AssistantInterrupted = "assistant-interrupted";
    public const string ToolCallRequested = "tool-call-requested";
    public const string ToolCallCompleted = "tool-call-completed";

    /// <summary>
    ///     A non-fatal turn notice (model substitution, tool disabled, history truncated) surfaced alongside the
    ///     content stream. See <see cref="XE_Local_AI_Engine.Client.Services.Events.TurnNoticePayload" />.
    /// </summary>
    public const string AssistantNotice = "assistant-notice";

    /// <summary>
    ///     A tool-approval request the in-flight turn is paused on, carrying the tool-call id that correlates it to
    ///     the waiting card, the tool name and the id the browser echoes to the resolve endpoint.
    /// </summary>
    /// <remarks>Its status stays <c>streaming</c> and no content rides it.</remarks>
    public const string ApprovalRequested = "approval-requested";

    /// <summary>
    ///     An <c>ask_user</c> question the in-flight turn is paused on, shaped like an approval request.
    /// </summary>
    /// <remarks>
    ///     Unlike an approval it also carries the <see cref="ChatStreamEvent.Questions" /> themselves, because a
    ///     client cannot render an answerable prompt from a correlation id alone. Its status stays <c>streaming</c>
    ///     and no content rides it.
    /// </remarks>
    public const string QuestionRequested = "question-requested";
}

public sealed record NodeChatStreamRequest(
    Guid ConversationId,
    string Content,
    Guid? UserMessageId = null,
    Guid? MessageId = null,
    Guid? RequestId = null,
    string? Model = null,
    bool UseLocalTools = false,
    string? ReasoningEffort = null,
    IReadOnlyDictionary<Guid, Guid>? SelectedPath = null,
    // The per-send selected agent, which takes precedence over the conversation binding; null falls back to that
    // binding, then to the seeded Default Assistant.
    Guid? AgentDefinitionId = null,
    // Developer-gated per-send sampling overrides. Null (the default) keeps the no-override path byte-identical to
    // today; the SignalR hub forwards the record unchanged.
    SamplingOptions? SamplingOptions = null,
    // The conversation's uploaded-file attachments for this turn, re-sent by the client each time. Plain chat inlines
    // their capped extracted text; agent mode reads them through the file tools and ignores this.
    IReadOnlyList<Guid>? AttachmentFileIds = null,
    // Opt-in knowledge grounding for a PLAIN-chat turn, off by default: the send path inlines the top-k fused hits as
    // ONE fenced region and records their provenance. Ignored in agent mode, which uses the gated tool instead.
    bool UseKnowledgeBase = false,
    // Whether ReasoningEffort above is a PIN rather than a preference, which reverses the usual precedence: a
    // development-workflow node's authored effort IS its session's pin, with no composer behind it.
    bool ReasoningEffortOverridesAgentPin = false,
    // Whether this turn is a step of a supervised work session rather than a send someone typed, set UNCONDITIONALLY
    // by the supervisor. The runtime package refuses the adaptive-effort model swap on it, since a step is autonomous.
    bool IsWorkSessionTurn = false,
    // Invariant GRAPH-C4-2, armed by a work session whose node declares no WriteExecute capability: the send refuses
    // if its own resolved offer carries a writing tool. See docs/wiki/05-chat.md, "Work-session write declaration".
    bool RefuseUndeclaredWrites = false,
    // Whether this turn is sent WITHOUT ask_user, overriding AskUserToolOffer's invariant for a workflow-owned work
    // session, which has no operator. See docs/wiki/05-chat.md, "Withdrawing ask_user from a workflow-owned session".
    bool SuppressAskUser = false);

public sealed record ChatStreamEvent
{
    public required string Type { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required string Status { get; init; }

    public required long Sequence { get; init; }

    public required long OccurredAtUtc { get; init; }

    public string? Delta { get; init; }

    public string? ReasoningDelta { get; init; }

    public string? Content { get; init; }

    public string? Reasoning { get; init; }

    public string? Error { get; init; }

    public string? Model { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? TotalTokens { get; init; }

    public int? ReasoningTokens { get; init; }

    public string? ToolCallId { get; init; }

    public string? ToolName { get; init; }

    public string? Arguments { get; init; }

    public bool? RequiresApproval { get; init; }

    public string? Result { get; init; }

    public bool? IsError { get; init; }

    public string? NoticeKind { get; init; }

    public string? NoticeMessage { get; init; }

    public string? RuntimePhase { get; init; }

    public string? ApprovalRequestId { get; init; }

    public string? QuestionRequestId { get; init; }

    public string? Questions { get; init; }

    public long? ContentOffset { get; init; }

    public long? ReasoningOffset { get; init; }

    public int? InvocationTimeoutSeconds { get; init; }

    public bool? SessionScopeEligible { get; init; }

    public string? NoticeDetail { get; init; }

    public string? RuntimePhaseChangedAtUtc { get; init; }
}
