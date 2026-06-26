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
    public const string AssistantDelta = "assistant-delta";
    public const string AssistantCompleted = "assistant-completed";
    public const string AssistantCancelled = "assistant-cancelled";
    public const string AssistantFailed = "assistant-failed";
    public const string AssistantInterrupted = "assistant-interrupted";
    public const string ToolCallRequested = "tool-call-requested";
    public const string ToolCallCompleted = "tool-call-completed";
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
    bool? IsError = null);
