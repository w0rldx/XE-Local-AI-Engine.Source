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
    ///     A pre-first-token runtime-phase transition (preparing the runtime / loading the model). Carries no content —
    ///     only <see cref="ChatStreamEvent.RuntimePhase" /> — so the client can show "Loading model…" during a cold load
    ///     instead of the generic typing indicator. Emitted only while a local model warms; absent for cloud/Ollama.
    /// </summary>
    public const string AssistantPhase = "assistant-phase";

    public const string AssistantDelta = "assistant-delta";
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
    // The per-send selected agent (composer agent mode). Takes precedence over the legacy conversation binding; null
    // falls back to the conversation binding, then to the seeded Default Assistant (mode-off persona). Trailing
    // optional so the SignalR hub forwards the record unchanged.
    Guid? AgentDefinitionId = null,
    // Developer-gated per-send sampling overrides. Null (the default) keeps the no-override path byte-identical to
    // today; the SignalR hub forwards the record unchanged.
    SamplingOptions? SamplingOptions = null,
    // The conversation's uploaded-file attachments to ground this turn on. In plain chat (no tools) the extracted text
    // of these files is inlined (capped) into the context; in agent mode they are read via the file tools, so this is
    // ignored. The client re-sends the conversation's current attachment ids each turn. Trailing optional so the
    // SignalR hub forwards the record unchanged.
    IReadOnlyList<Guid>? AttachmentFileIds = null);

public sealed record ChatStreamEvent(
    string Type,
    Guid ConversationId,
    Guid MessageId,
    Guid RequestId,
    string Status,
    long Sequence,
    long OccurredAtUtc,
    string? Delta = null,
    string? ReasoningDelta = null,
    string? Content = null,
    string? Reasoning = null,
    string? Error = null,
    string? Model = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? TotalTokens = null,
    int? ReasoningTokens = null,
    string? ToolCallId = null,
    string? ToolName = null,
    string? Arguments = null,
    bool? RequiresApproval = null,
    string? Result = null,
    bool? IsError = null,
    // Non-fatal turn notice fields (AssistantNotice events only). NoticeKind is the TurnNoticeKind enum name (e.g.
    // "ModelSubstituted", "ToolDisabled", "HistoryTruncated"); NoticeMessage is the sanitized, user-facing text.
    // Trailing optional so every existing event type's wire shape is unchanged.
    string? NoticeKind = null,
    string? NoticeMessage = null,
    // Runtime phase (AssistantPhase events only): the wire form of InvocationRuntimePhase — "preparing_runtime",
    // "loading_model", or "generating" — so the client can show a distinct model-loading indicator before the first
    // token. Trailing optional so every existing event type's wire shape is unchanged.
    string? RuntimePhase = null);
