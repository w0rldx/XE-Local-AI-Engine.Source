namespace XE_Local_AI_Engine.Cli.Streaming;

/// <summary>The hub's <c>ChatStreamEvent.Type</c> values, copied from the server's <c>ChatStreamEventTypes</c>.</summary>
internal static class ChatStreamEventTypes
{
    public const string UserMessagePersisted = "user-message-persisted";
    public const string AssistantPending = "assistant-pending";
    public const string AssistantQueued = "assistant-queued";
    public const string AssistantStreaming = "assistant-streaming";
    public const string AssistantPhase = "assistant-phase";
    public const string AssistantDelta = "assistant-delta";
    public const string AssistantSnapshot = "assistant-snapshot";
    public const string AssistantReconcile = "assistant-reconcile";
    public const string AssistantCompleted = "assistant-completed";
    public const string AssistantCancelled = "assistant-cancelled";
    public const string AssistantFailed = "assistant-failed";
    public const string AssistantInterrupted = "assistant-interrupted";
    public const string ToolCallRequested = "tool-call-requested";
    public const string ToolCallCompleted = "tool-call-completed";
    public const string AssistantNotice = "assistant-notice";
    public const string ApprovalRequested = "approval-requested";
    public const string QuestionRequested = "question-requested";
}
