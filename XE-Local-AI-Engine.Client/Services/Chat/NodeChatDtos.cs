namespace XE_Local_AI_Engine.Client.Services.Chat;

public static class NodeChatMessageStatusValues
{
    public const string Pending = "pending";
    public const string Streaming = "streaming";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Streaming,
        Completed,
        Cancelled,
        Failed,
        Interrupted
    };
}

public sealed record NodeChatCreateConversationRequest(
    string? Title,
    string? UserId,
    long CreatedAtUtc);

public sealed record NodeChatListConversationsRequest(
    bool IncludeArchived = false,
    int? Limit = null);

public sealed record NodeChatConversationSummaryDto(
    Guid ConversationId,
    string? Title,
    long CreatedAtUtc,
    long LastSeenUtc,
    string? LastMessagePreview,
    string? LastMessageStatus,
    bool Purged);

public sealed record NodeChatConversationDto(
    Guid ConversationId,
    string? Title,
    string? UserId,
    long CreatedAtUtc,
    long LastSeenUtc,
    bool Purged,
    IReadOnlyList<NodeChatPersistedMessageDto> Messages);

public sealed record NodeChatPersistUserMessageRequest(
    Guid ConversationId,
    Guid MessageId,
    string Content,
    long CreatedAtUtc,
    string? MetadataJson = null);

public sealed record NodeChatCreateAssistantPlaceholderRequest(
    Guid ConversationId,
    Guid MessageId,
    Guid RequestId,
    long CreatedAtUtc,
    string? Model = null,
    string? MetadataJson = null);

public sealed record NodeChatMessageCorrelation(
    Guid ConversationId,
    Guid MessageId,
    Guid RequestId);

public sealed record NodeChatPartialFlushRequest(
    NodeChatMessageCorrelation Correlation,
    string Content,
    string? Reasoning,
    long UpdatedAtUtc,
    bool ReplaceContent = true);

public sealed record NodeChatTerminalizeMessageRequest(
    NodeChatMessageCorrelation Correlation,
    string Status,
    long UpdatedAtUtc,
    string? Content = null,
    string? Reasoning = null,
    string? Error = null,
    string? Model = null,
    int? InputCount = null,
    int? OutputCount = null,
    int? TotalCount = null,
    int? ReasoningCount = null);

public sealed record NodeChatCancelRequest(
    NodeChatMessageCorrelation Correlation,
    long CancelledAtUtc);

public sealed record NodeChatDeleteConversationRequest(
    Guid ConversationId,
    long DeletedAtUtc,
    bool PurgeImmediately = false);

public sealed record NodeChatPersistedMessageDto(
    Guid MessageId,
    Guid ConversationId,
    Guid? RequestId,
    int Sequence,
    string Role,
    string Content,
    string? Reasoning,
    string Status,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    string? Model,
    string? Error,
    string? MetadataJson,
    int? InputCount = null,
    int? OutputCount = null,
    int? TotalCount = null,
    int? ReasoningCount = null);

public sealed record NodeChatCancelResultDto(
    NodeChatMessageCorrelation Correlation,
    string Status,
    bool Cancelled);

public sealed record NodeChatDeleteResultDto(
    Guid ConversationId,
    bool CancelRequested,
    bool Purged);
