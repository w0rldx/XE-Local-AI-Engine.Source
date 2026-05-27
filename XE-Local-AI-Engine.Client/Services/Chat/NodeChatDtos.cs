namespace XE_Local_AI_Engine.Client.Services.Chat;

public static class NodeChatOriginValues
{
    public const string Local = "Local";
    public const string Remote = "Remote";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Local,
        Remote
    };
}

public static class NodeChatMessageStatusValues
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Streaming = "streaming";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending,
        Queued,
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
    long CreatedAtUtc,
    string Origin = NodeChatOriginValues.Local);

public sealed record NodeChatEnsureConversationRequest(
    Guid ConversationId,
    string? Title,
    string? UserId,
    long CreatedAtUtc,
    string Origin = NodeChatOriginValues.Local);

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
    bool Purged,
    string Origin = NodeChatOriginValues.Local,
    bool IsPinned = false,
    bool Archived = false);

public sealed record NodeChatConversationDto(
    Guid ConversationId,
    string? Title,
    string? UserId,
    long CreatedAtUtc,
    long LastSeenUtc,
    bool Purged,
    IReadOnlyList<NodeChatPersistedMessageDto> Messages,
    string Origin = NodeChatOriginValues.Local,
    bool IsPinned = false,
    bool Archived = false,
    Guid? BranchOfConversationId = null);

public sealed record NodeChatPersistUserMessageRequest(
    Guid ConversationId,
    Guid MessageId,
    string Content,
    long CreatedAtUtc,
    string? MetadataJson = null,
    string Origin = NodeChatOriginValues.Local);

public sealed record NodeChatCreateAssistantPlaceholderRequest(
    Guid ConversationId,
    Guid MessageId,
    Guid RequestId,
    long CreatedAtUtc,
    string? Model = null,
    string? MetadataJson = null,
    string Origin = NodeChatOriginValues.Local);

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

public sealed record NodeChatRenameConversationRequest(
    Guid ConversationId,
    string? Title,
    long UpdatedAtUtc);

public sealed record NodeChatSetConversationPinnedRequest(
    Guid ConversationId,
    bool IsPinned,
    long UpdatedAtUtc);

public sealed record NodeChatSetConversationArchivedRequest(
    Guid ConversationId,
    bool Archived,
    long UpdatedAtUtc);

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
    int? ReasoningCount = null,
    string Origin = NodeChatOriginValues.Local,
    Guid? ParentMessageId = null,
    Guid? VariantGroupId = null,
    string? FeedbackRating = null,
    string? FeedbackComment = null);

public sealed record NodeChatCancelResultDto(
    NodeChatMessageCorrelation Correlation,
    string Status,
    bool Cancelled);

public sealed record NodeChatDeleteResultDto(
    Guid ConversationId,
    bool CancelRequested,
    bool Purged);

public static class NodeChatFeedbackRatingValues
{
    public const string Up = "up";
    public const string Down = "down";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Up,
        Down
    };
}

/// <summary>
/// Branch (Phase 5.1): clones the source conversation's messages up to and including <see cref="MessageId"/>
/// into a NEW conversation. The new conversation is Origin=Local and records
/// <c>branch_of_conversation_id</c> = source for provenance.
/// </summary>
public sealed record NodeChatBranchConversationRequest(
    Guid ConversationId,
    Guid MessageId,
    long CreatedAtUtc);

public sealed record NodeChatBranchResultDto(
    Guid SourceConversationId,
    Guid BranchedConversationId,
    int CopiedMessageCount);

/// <summary>
/// Revision (Phase 5.2): records a regenerated assistant turn as a SIBLING VARIANT (never an in-place
/// overwrite). All variants of one logical turn share a <c>variant_group_id</c>; <see cref="ParentMessageId"/>
/// is the user turn the variants answer. When <see cref="VariantGroupId"/> is null a fresh group is minted and
/// the originating message is back-stamped into it.
/// </summary>
public sealed record NodeChatCreateMessageVariantRequest(
    Guid ConversationId,
    Guid OriginalMessageId,
    Guid NewMessageId,
    Guid RequestId,
    long CreatedAtUtc,
    string? Model = null,
    string? MetadataJson = null);

public sealed record NodeChatMessageVariantDto(
    Guid VariantGroupId,
    Guid OriginalMessageId,
    NodeChatPersistedMessageDto Variant);

public sealed record NodeChatSetMessageFeedbackRequest(
    Guid ConversationId,
    Guid MessageId,
    string Rating,
    string? Comment,
    long UpdatedAtUtc);

public sealed record NodeChatMessageFeedbackDto(
    Guid MessageId,
    Guid ConversationId,
    string Rating,
    string? Comment,
    long CreatedAtUtc,
    long UpdatedAtUtc);
