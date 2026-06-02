namespace XE_Local_AI_Engine.Client.Services.Chat;

public interface INodeChatPersistenceService
{
    Task<NodeChatConversationDto> CreateConversationAsync(NodeChatCreateConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Idempotent upsert for the caller-supplied conversation id. If a row already exists (any purged state) it
    ///     is returned unchanged — title/origin/timestamps are NOT overwritten. Otherwise a new row is inserted.
    ///     Used by the platform path (which has no pre-existing local conversation row) before persisting the
    ///     synthesized user + assistant messages. See Plans/schema-contract-sheet.md §3.
    /// </summary>
    Task<NodeChatConversationDto> EnsureConversationAsync(NodeChatEnsureConversationRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatConversationDto?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Lightweight origin lookup (no messages loaded). Returns the conversation's origin ("Local"/"Remote"), or
    ///     null when the conversation does not exist. Used by the Origin=Remote mutation guard.
    /// </summary>
    Task<string?> GetConversationOriginAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads the conversation's selected-path map {variantGroupId-&gt;selectedMessageId}, or null when none has been
    ///     persisted or the conversation does not exist. Selection metadata only — node-agnostic and E2E-safe.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>?> GetSelectedPathAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Upserts the conversation's selected-path map {variantGroupId-&gt;selectedMessageId}. An empty map clears the
    ///     stored selection (column set to null). Returns the persisted map.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid>> SetSelectedPathAsync(NodeChatSetSelectedPathRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatPersistedMessageDto> PersistUserMessageAsync(NodeChatPersistUserMessageRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatPersistedMessageDto> CreateAssistantPlaceholderAsync(NodeChatCreateAssistantPlaceholderRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatPersistedMessageDto> MarkAssistantQueuedAsync(NodeChatMessageCorrelation correlation, long updatedAtUtc, CancellationToken cancellationToken = default);

    Task<NodeChatPersistedMessageDto> MarkAssistantStreamingAsync(NodeChatMessageCorrelation correlation, long updatedAtUtc, CancellationToken cancellationToken = default);

    Task<NodeChatPersistedMessageDto> FlushAssistantPartialAsync(NodeChatPartialFlushRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatPersistedMessageDto> TerminalizeAssistantMessageAsync(NodeChatTerminalizeMessageRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatCancelResultDto> CancelMessageAsync(NodeChatCancelRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatDeleteResultDto> DeleteConversationAsync(NodeChatDeleteConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Renames a conversation. Returns the updated conversation, or null if not found.</summary>
    Task<NodeChatConversationDto?> RenameConversationAsync(NodeChatRenameConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Pins or unpins a conversation. Returns the updated conversation, or null if not found.</summary>
    Task<NodeChatConversationDto?> SetConversationPinnedAsync(NodeChatSetConversationPinnedRequest request, CancellationToken cancellationToken = default);

    /// <summary>Archives or unarchives a conversation, distinct from purge. Returns the updated conversation, or null if not found.</summary>
    Task<NodeChatConversationDto?> SetConversationArchivedAsync(NodeChatSetConversationArchivedRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Branches a conversation: clones every message up to and including the target message into a
    ///     NEW Origin=Local conversation that records <c>branch_of_conversation_id</c> = source. Returns null when the
    ///     source conversation or target message does not exist.
    /// </summary>
    Task<NodeChatBranchResultDto?> BranchConversationAsync(NodeChatBranchConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records a regenerated assistant turn as a SIBLING VARIANT — never an in-place overwrite. The
    ///     new message shares a <c>variant_group_id</c> with the original (minted and back-stamped onto the original
    ///     when it had none) and copies the original's <c>parent_message_id</c>. Returns null when the original message
    ///     does not exist.
    /// </summary>
    Task<NodeChatMessageVariantDto?> CreateMessageVariantAsync(NodeChatCreateMessageVariantRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists all variants of the logical turn that the given message belongs to, ordered by sequence.</summary>
    Task<IReadOnlyList<NodeChatPersistedMessageDto>> ListMessageVariantsAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Upserts node-local feedback (thumbs + optional comment) for a message. One row per message.</summary>
    Task<NodeChatMessageFeedbackDto> SetMessageFeedbackAsync(NodeChatSetMessageFeedbackRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the stored feedback for a message, or null when none has been recorded.</summary>
    Task<NodeChatMessageFeedbackDto?> GetMessageFeedbackAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default);
}
