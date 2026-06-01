namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Request DTO for create node chat conversation operations.
/// </summary>
public sealed class CreateNodeChatConversationRequest
{
    public string? Title { get; init; }

    public string? UserId { get; init; }

    /// <summary>
    ///     Optional binding to a node-local agent definition. When set, the new conversation runs the bound
    ///     definition's persona/tools/model; null (the default) keeps the implicit default chat persona.
    /// </summary>
    public Guid? AgentDefinitionId { get; init; }
}

/// <summary>
///     Request DTO for list node chat conversations operations.
/// </summary>
public sealed class ListNodeChatConversationsRequest
{
    public bool IncludeArchived { get; init; }

    public int? Limit { get; init; }
}

/// <summary>
///     Response DTO for list node chat conversations operations.
/// </summary>
public sealed class ListNodeChatConversationsResponse
{
    public required IReadOnlyList<NodeChatConversationSummaryResponse> Items { get; init; }
}

/// <summary>
///     Request DTO for get node chat conversation operations.
/// </summary>
public sealed class GetNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }
}

/// <summary>
///     Request DTO for delete node chat conversation operations.
/// </summary>
public sealed class DeleteNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool PurgeImmediately { get; init; }
}

/// <summary>
///     Request DTO for rename node chat conversation operations.
/// </summary>
public sealed class RenameNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public string? Title { get; init; }
}

/// <summary>
///     Request DTO for pin node chat conversation operations.
/// </summary>
public sealed class PinNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool IsPinned { get; init; }
}

/// <summary>
///     Request DTO for archive node chat conversation operations.
/// </summary>
public sealed class ArchiveNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool Archived { get; init; }
}

/// <summary>
///     Request DTO for cancel node chat message operations.
/// </summary>
public sealed class CancelNodeChatMessageRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public Guid RequestId { get; init; }
}

/// <summary>
///     Request DTO for branch node chat conversation operations.
/// </summary>
public sealed class BranchNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

/// <summary>
///     Request DTO for list node chat message revisions operations.
/// </summary>
public sealed class ListNodeChatMessageRevisionsRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

/// <summary>
///     Request DTO for set node chat message feedback operations.
/// </summary>
public sealed class SetNodeChatMessageFeedbackRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public required string Rating { get; init; }

    public string? Comment { get; init; }
}

/// <summary>
///     Request DTO for get node chat message feedback operations.
/// </summary>
public sealed class GetNodeChatMessageFeedbackRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

/// <summary>
///     Request DTO for set node chat selected path operations.
/// </summary>
public sealed class SetNodeChatSelectedPathRequest
{
    public Guid ConversationId { get; init; }

    public IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }
}

/// <summary>
///     Response DTO for node chat conversation summary operations.
/// </summary>
public sealed class NodeChatConversationSummaryResponse
{
    public required Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public string? LastMessagePreview { get; init; }

    public string? LastMessageStatus { get; init; }

    public required bool Purged { get; init; }

    public required string Origin { get; init; }

    public required bool IsPinned { get; init; }

    public required bool Archived { get; init; }
}

/// <summary>
///     Response DTO for node chat conversation operations.
/// </summary>
public sealed class NodeChatConversationResponse
{
    public required Guid ConversationId { get; init; }

    public string? Title { get; init; }

    public string? UserId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long LastSeenUtc { get; init; }

    public required bool Purged { get; init; }

    public required string Origin { get; init; }

    public required bool IsPinned { get; init; }

    public required bool Archived { get; init; }

    public Guid? BranchOfConversationId { get; init; }

    public IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }

    public required IReadOnlyList<NodeChatMessageResponse> Messages { get; init; }
}

/// <summary>
///     Response DTO for node chat message operations.
/// </summary>
public sealed class NodeChatMessageResponse
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public Guid? RequestId { get; init; }

    public required int Sequence { get; init; }

    public required string Role { get; init; }

    public required string Content { get; init; }

    public string? Reasoning { get; init; }

    public required string Status { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required string Origin { get; init; }

    public string? Model { get; init; }

    public string? Error { get; init; }

    public int? InputTokens { get; init; }

    public int? OutputTokens { get; init; }

    public int? TotalTokens { get; init; }

    public int? ReasoningTokens { get; init; }

    public Guid? ParentMessageId { get; init; }

    public Guid? VariantGroupId { get; init; }

    public string? FeedbackRating { get; init; }

    public string? FeedbackComment { get; init; }

    /// <summary>
    ///     Ordered interleave of reasoning segments and tool cards (serialized as <c>parts</c>). Null for legacy
    ///     messages persisted before parts existed; the client synthesizes a single Thoughts block from
    ///     <see cref="Reasoning" /> in that case.
    /// </summary>
    public IReadOnlyList<NodeChatMessagePart>? Parts { get; init; }
}

/// <summary>
///     Response DTO for node chat cancel message operations.
/// </summary>
public sealed class NodeChatCancelMessageResponse
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required string Status { get; init; }

    public required bool Cancelled { get; init; }
}

/// <summary>
///     Response DTO for node chat delete conversation operations.
/// </summary>
public sealed class NodeChatDeleteConversationResponse
{
    public required Guid ConversationId { get; init; }

    public required bool CancelRequested { get; init; }

    public required bool Purged { get; init; }
}

/// <summary>
///     Response DTO for node chat branch conversation operations.
/// </summary>
public sealed class NodeChatBranchConversationResponse
{
    public required Guid SourceConversationId { get; init; }

    public required Guid BranchedConversationId { get; init; }

    public required int CopiedMessageCount { get; init; }
}

/// <summary>
///     Response DTO for node chat message revisions operations.
/// </summary>
public sealed class NodeChatMessageRevisionsResponse
{
    public required Guid MessageId { get; init; }

    public Guid? VariantGroupId { get; init; }

    public required IReadOnlyList<NodeChatMessageResponse> Variants { get; init; }
}

/// <summary>
///     Response DTO for node chat message feedback operations.
/// </summary>
public sealed class NodeChatMessageFeedbackResponse
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string Rating { get; init; }

    public string? Comment { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

/// <summary>
///     Response DTO for node chat selected path operations.
/// </summary>
public sealed class NodeChatSelectedPathResponse
{
    public required Guid ConversationId { get; init; }

    public required IReadOnlyDictionary<Guid, Guid> SelectedPath { get; init; }
}

/// <summary>
///     409 Conflict body returned when a mutation targets a read-only (Origin=Remote) conversation.
/// </summary>
public sealed class NodeChatConflictResponse
{
    public required string Code { get; init; }

    public required string Reason { get; init; }

    /// <summary>Shared 409 body for read-only (Origin=Remote) conversation rejections.</summary>
    public static NodeChatConflictResponse ReadOnly { get; } = new()
    {
        Code = NodeChatReadOnlyConversationException.Code,
        Reason = NodeChatReadOnlyConversationException.Reason
    };
}
