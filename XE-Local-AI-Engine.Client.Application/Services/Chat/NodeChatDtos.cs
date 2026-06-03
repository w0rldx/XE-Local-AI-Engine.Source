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

public sealed record NodeChatCreateConversationRequest(
    string? Title,
    string? UserId,
    long CreatedAtUtc,
    string Origin = NodeChatOriginValues.Local,
    Guid? AgentDefinitionId = null);

public sealed record NodeChatEnsureConversationRequest(
    Guid ConversationId,
    string? Title,
    string? UserId,
    long CreatedAtUtc,
    string Origin = NodeChatOriginValues.Local);

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
    string Origin = NodeChatOriginValues.Local,
    // Per-response agent attribution stamped at send time. Threaded into the metadata blob (no DB column) so the
    // pending placeholder already carries the agent name; null on cold/fallback paths (client renders the localized
    // "Default Assistant" label).
    Guid? AgentDefinitionId = null,
    string? AgentName = null);

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
    int? ReasoningCount = null,
    // Ordered interleave assembled from the run's reasoning segments + tool lifecycle. Null leaves any existing parts
    // untouched; an empty list is a meaningful "no parts" (e.g. a plain-text turn) and overwrites.
    IReadOnlyList<NodeChatMessagePart>? Parts = null);

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

/// <summary>
///     Represents the kind of an ordered assistant message part. The interleaved render region (reasoning segments
///     and tool cards, ordered by <see cref="NodeChatMessagePart.Sequence" />) is reconstructed from these parts on
///     reload so the live and reloaded views are identical. <c>text</c> covers the rarer mid-turn narration case.
/// </summary>
public static class NodeChatMessagePartKinds
{
    public const string Reasoning = "reasoning";
    public const string Tool = "tool";
    public const string Text = "text";
}

/// <summary>
///     Represents the lifecycle state of a tool part. Mirrors the client tool-call state union; persisted tool parts
///     carry the terminal state (<see cref="Received" /> or <see cref="Failed" />) once the tool has completed.
/// </summary>
public static class NodeChatToolPartStates
{
    public const string Requesting = "requesting";
    public const string Waiting = "waiting";
    public const string Received = "received";
    public const string Failed = "failed";
}

/// <summary>
///     One ordered part of an assistant turn: a reasoning segment, a tool call (collapsed requested-&gt;completed by
///     <see cref="ToolCallId" />, including its result), or an interleaved text segment. Persisted in the
///     <c>metadata_json</c> column alongside the flattened <c>Reasoning</c> so reload restores the exact interleave.
///     That column is written via raw ADO.NET (<c>Encoding.UTF8.GetBytes</c>), the same plaintext-at-rest posture as
///     the pre-existing reasoning/model/token fields on this path (single-user device; documented in
///     <c>NodeChatPersistenceServiceTests</c>). Parts add no new exposure beyond what reasoning already carries.
///     Optional fields are null for the kinds that do not use them (e.g. a reasoning part has only
///     <see cref="Text" />).
/// </summary>
public sealed record NodeChatMessagePart(
    string Kind,
    int Sequence,
    string? Text = null,
    string? ToolCallId = null,
    string? Name = null,
    string? State = null,
    string? Args = null,
    string? Result = null,
    bool? RequiresApproval = null);

/// <summary>
///     Transport DTO for node chat persisted message data. <c>Parts</c> is the ordered interleave (reasoning segments
///     plus tool cards); it is null for legacy messages persisted before parts existed, in which case the client
///     synthesizes a single Thoughts block from <c>Reasoning</c>.
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
    string? FeedbackComment = null,
    IReadOnlyList<NodeChatMessagePart>? Parts = null,
    // Per-response agent attribution snapshot, surfaced from the metadata blob (no DB column). AgentDefinitionId is the
    // provenance of the agent that produced the turn; AgentName is its display-name snapshot at send time (survives a
    // later rename/delete). Both are null for legacy turns persisted before agent mode existed.
    Guid? AgentDefinitionId = null,
    string? AgentName = null) : ISelectedPathMessage;

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
    string? MetadataJson = null,
    // Per-response agent attribution for the regenerated variant, stamped at mint time (re-resolved → picks up a
    // rename; falls back to the original's stored name when the agent was deleted). Same metadata-blob path as the
    // send placeholder; trailing optional so existing callers are unaffected.
    Guid? AgentDefinitionId = null,
    string? AgentName = null);

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
