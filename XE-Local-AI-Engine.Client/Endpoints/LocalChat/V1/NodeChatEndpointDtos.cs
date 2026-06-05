namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using XE_Local_AI_Engine.Client.Services.Chat;

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

public sealed class ListNodeChatConversationsRequest
{
    public bool IncludeArchived { get; init; }

    public int? Limit { get; init; }
}

public sealed class ListNodeChatConversationsResponse
{
    public required IReadOnlyList<NodeChatConversationSummaryResponse> Items { get; init; }
}

public sealed class GetNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }
}

public sealed class DeleteNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool PurgeImmediately { get; init; }
}

public sealed class RenameNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public string? Title { get; init; }
}

public sealed class PinNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool IsPinned { get; init; }
}

public sealed class ArchiveNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public bool Archived { get; init; }
}

public sealed class CancelNodeChatMessageRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public Guid RequestId { get; init; }
}

public sealed class BranchNodeChatConversationRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

public sealed class ListNodeChatMessageRevisionsRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

public sealed class SetNodeChatMessageFeedbackRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }

    public required string Rating { get; init; }

    public string? Comment { get; init; }
}

public sealed class GetNodeChatMessageFeedbackRequest
{
    public Guid ConversationId { get; init; }

    public Guid MessageId { get; init; }
}

public sealed class SetNodeChatSelectedPathRequest
{
    public Guid ConversationId { get; init; }

    public IReadOnlyDictionary<Guid, Guid>? SelectedPath { get; init; }
}

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

    /// <summary>
    ///     The provenance of the agent that produced this assistant turn (per-response attribution). Null for legacy
    ///     turns persisted before agent mode existed and for user messages.
    /// </summary>
    public Guid? AgentDefinitionId { get; init; }

    /// <summary>
    ///     The display-name snapshot of the agent that produced this assistant turn (survives a later agent
    ///     rename/delete). Null for legacy turns and user messages; the client renders the localized "Default
    ///     Assistant" fallback label in that case.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    ///     The reasoning effort actually used to generate this assistant turn (e.g. "none", "low", "medium", "high").
    ///     Null for legacy turns persisted before this field existed and for user messages.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    ///     Whole-turn wall-clock generation duration in milliseconds, used with <see cref="OutputTokens" /> to compute
    ///     the optional tokens-per-second attribution. Null for legacy turns persisted before this field existed, the
    ///     platform path, and user messages.
    /// </summary>
    public long? GenerationDurationMs { get; init; }
}

public sealed class NodeChatCancelMessageResponse
{
    public required Guid ConversationId { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid RequestId { get; init; }

    public required string Status { get; init; }

    public required bool Cancelled { get; init; }
}

public sealed class NodeChatDeleteConversationResponse
{
    public required Guid ConversationId { get; init; }

    public required bool CancelRequested { get; init; }

    public required bool Purged { get; init; }
}

public sealed class NodeChatBranchConversationResponse
{
    public required Guid SourceConversationId { get; init; }

    public required Guid BranchedConversationId { get; init; }

    public required int CopiedMessageCount { get; init; }
}

public sealed class NodeChatMessageRevisionsResponse
{
    public required Guid MessageId { get; init; }

    public Guid? VariantGroupId { get; init; }

    public required IReadOnlyList<NodeChatMessageResponse> Variants { get; init; }
}

public sealed class NodeChatMessageFeedbackResponse
{
    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string Rating { get; init; }

    public string? Comment { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

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
