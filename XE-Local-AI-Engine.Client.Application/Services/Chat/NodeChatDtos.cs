namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Represents node chat origin values.
/// </summary>
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

/// <summary>
///     Represents node chat message status values.
/// </summary>
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

/// <summary>
///     Request DTO for node chat create conversation operations.
/// </summary>
public sealed record NodeChatCreateConversationRequest(
    string? Title,
    string? UserId,
    long CreatedAtUtc,
    string Origin = NodeChatOriginValues.Local,
    Guid? AgentDefinitionId = null);

/// <summary>
///     Request DTO for node chat ensure conversation operations.
/// </summary>
public sealed record NodeChatEnsureConversationRequest(
    Guid ConversationId,
    string? Title,
    string? UserId,
    long CreatedAtUtc,
    string Origin = NodeChatOriginValues.Local);

/// <summary>
///     Request DTO for node chat list conversations operations.
/// </summary>
public sealed record NodeChatListConversationsRequest(
    bool IncludeArchived = false,
    int? Limit = null);

/// <summary>
///     Transport DTO for node chat conversation summary data.
/// </summary>
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

/// <summary>
///     Transport DTO for node chat conversation data.
/// </summary>
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
    Guid? BranchOfConversationId = null,
    IReadOnlyDictionary<Guid, Guid>? SelectedPath = null,
    Guid? AgentDefinitionId = null);

/// <summary>
///     Request DTO for node chat persist user message operations.
/// </summary>
public sealed record NodeChatPersistUserMessageRequest(
    Guid ConversationId,
    Guid MessageId,
    string Content,
    long CreatedAtUtc,
    string? MetadataJson = null,
    string Origin = NodeChatOriginValues.Local);

/// <summary>
///     Request DTO for node chat create assistant placeholder operations.
/// </summary>
public sealed record NodeChatCreateAssistantPlaceholderRequest(
    Guid ConversationId,
    Guid MessageId,
    Guid RequestId,
    long CreatedAtUtc,
    string? Model = null,
    string? MetadataJson = null,
    string Origin = NodeChatOriginValues.Local);

/// <summary>
///     Value object carrying node chat message correlation data.
/// </summary>
public sealed record NodeChatMessageCorrelation(
    Guid ConversationId,
    Guid MessageId,
    Guid RequestId);

/// <summary>
///     Request DTO for node chat partial flush operations.
/// </summary>
public sealed record NodeChatPartialFlushRequest(
    NodeChatMessageCorrelation Correlation,
    string Content,
    string? Reasoning,
    long UpdatedAtUtc,
    bool ReplaceContent = true);

/// <summary>
///     Request DTO for node chat terminalize message operations.
/// </summary>
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

/// <summary>
///     Request DTO for node chat cancel operations.
/// </summary>
public sealed record NodeChatCancelRequest(
    NodeChatMessageCorrelation Correlation,
    long CancelledAtUtc);

/// <summary>
///     Request DTO for node chat delete conversation operations.
/// </summary>
public sealed record NodeChatDeleteConversationRequest(
    Guid ConversationId,
    long DeletedAtUtc,
    bool PurgeImmediately = false);

/// <summary>
///     Request DTO for node chat rename conversation operations.
/// </summary>
public sealed record NodeChatRenameConversationRequest(
    Guid ConversationId,
    string? Title,
    long UpdatedAtUtc);

/// <summary>
///     Request DTO for node chat set conversation pinned operations.
/// </summary>
public sealed record NodeChatSetConversationPinnedRequest(
    Guid ConversationId,
    bool IsPinned,
    long UpdatedAtUtc);

/// <summary>
///     Request DTO for node chat set conversation archived operations.
/// </summary>
public sealed record NodeChatSetConversationArchivedRequest(
    Guid ConversationId,
    bool Archived,
    long UpdatedAtUtc);

/// <summary>
///     Transport DTO for node chat persisted message data.
/// </summary>
public sealed record NodeChatPersistedMessageDto(
    Guid MessageId,
    Guid ConversationId,
    Guid? RequestId,
    int Sequence,
    // ISelectedPathMessage is satisfied by the existing MessageId/Sequence/VariantGroupId/CreatedAtUtc members,
    // so the SelectedPathResolver can collapse these messages to the selected variant path with no projection.
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
    string? FeedbackComment = null) : ISelectedPathMessage;

/// <summary>
///     Transport DTO for node chat cancel result data.
/// </summary>
public sealed record NodeChatCancelResultDto(
    NodeChatMessageCorrelation Correlation,
    string Status,
    bool Cancelled);

/// <summary>
///     Transport DTO for node chat delete result data.
/// </summary>
public sealed record NodeChatDeleteResultDto(
    Guid ConversationId,
    bool CancelRequested,
    bool Purged);

/// <summary>
///     Represents node chat feedback rating values.
/// </summary>
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
///     Conversation branch: clones the source conversation's messages up to and including <see cref="MessageId" />
///     into a NEW conversation. The new conversation is Origin=Local and records
///     <c>branch_of_conversation_id</c> = source for provenance.
/// </summary>
public sealed record NodeChatBranchConversationRequest(
    Guid ConversationId,
    Guid MessageId,
    long CreatedAtUtc);

/// <summary>
///     Transport DTO for node chat branch result data.
/// </summary>
public sealed record NodeChatBranchResultDto(
    Guid SourceConversationId,
    Guid BranchedConversationId,
    int CopiedMessageCount);

/// <summary>
///     Assistant revision: records a regenerated assistant turn as a SIBLING VARIANT (never an in-place
///     overwrite). All variants of one logical turn share a <c>variant_group_id</c>; <see cref="ParentMessageId" />
///     is the user turn the variants answer. When <see cref="VariantGroupId" /> is null a fresh group is minted and
///     the originating message is back-stamped into it.
/// </summary>
public sealed record NodeChatCreateMessageVariantRequest(
    Guid ConversationId,
    Guid OriginalMessageId,
    Guid NewMessageId,
    Guid RequestId,
    long CreatedAtUtc,
    string? Model = null,
    string? MetadataJson = null);

/// <summary>
///     Transport DTO for node chat message variant data.
/// </summary>
public sealed record NodeChatMessageVariantDto(
    Guid VariantGroupId,
    Guid OriginalMessageId,
    NodeChatPersistedMessageDto Variant);

/// <summary>
///     Persists the conversation's selected-path map {variantGroupId-&gt;selectedMessageId} (which sibling variant is
///     chosen on each branched turn). Selection metadata only — the conversation tree topology lives on the messages.
/// </summary>
public sealed record NodeChatSetSelectedPathRequest(
    Guid ConversationId,
    IReadOnlyDictionary<Guid, Guid>? SelectedPath,
    long UpdatedAtUtc);

/// <summary>
///     Request DTO for node chat set message feedback operations.
/// </summary>
public sealed record NodeChatSetMessageFeedbackRequest(
    Guid ConversationId,
    Guid MessageId,
    string Rating,
    string? Comment,
    long UpdatedAtUtc);

/// <summary>
///     Transport DTO for node chat message feedback data.
/// </summary>
public sealed record NodeChatMessageFeedbackDto(
    Guid MessageId,
    Guid ConversationId,
    string Rating,
    string? Comment,
    long CreatedAtUtc,
    long UpdatedAtUtc);
