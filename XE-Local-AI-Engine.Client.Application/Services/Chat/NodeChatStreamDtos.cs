namespace XE_Local_AI_Engine.Client.Services.Chat;

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
    IReadOnlyDictionary<Guid, Guid>? SelectedPath = null);

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
