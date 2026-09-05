namespace XE_Local_AI_Engine.Client.Services.Chat;

public interface INodeChatPersistenceService
{
    Task<NodeChatConversationDto> CreateConversationAsync(NodeChatCreateConversationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Idempotent upsert for the caller-supplied conversation id. If a row already exists (any purged state) it
    ///     is returned unchanged — title/origin/timestamps are NOT overwritten. Otherwise a new row is inserted.
    ///     Used by the platform path (which has no pre-existing local conversation row) before persisting the
    ///     synthesized user + assistant messages. The conversation id is caller-supplied (reused from the platform's
    ///     id), never minted here.
    /// </summary>
    Task<NodeChatConversationDto> EnsureConversationAsync(NodeChatEnsureConversationRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken = default);

    Task<NodeChatConversationDto?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The chat-turn read. Returns the same conversation as <see cref="GetConversationAsync" /> with the same message
    ///     STRUCTURE, but skips transferring, decrypting and parsing the content and metadata blobs of non-user messages
    ///     the conversation's compaction synopsis has already replaced — work the turn always threw away. Output-equivalent
    ///     for the turn's two consumers only (context assembly and memory extraction); the implementation's remarks carry
    ///     the equivalence argument. Anything that RENDERS or RE-PERSISTS a conversation must use
    ///     <see cref="GetConversationAsync" /> — and so must a turn that replays persisted tool history, which reads the
    ///     omitted metadata blob of exactly the covered rows this read blanks.
    /// </summary>
    Task<NodeChatConversationDto?> GetConversationForTurnAsync(Guid conversationId, CancellationToken cancellationToken = default);

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
    ///     Sets the conversation's temporary-chat (<c>memory_excluded</c>) flag — the per-conversation override of the
    ///     bound agent's default (adaptive memory). Returns the updated conversation, or null if not found.
    /// </summary>
    Task<NodeChatConversationDto?> SetConversationMemoryExcludedAsync(NodeChatSetConversationMemoryExcludedRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes (or clears with a null summary) the conversation's non-destructive compaction synopsis. Returns the
    ///     updated conversation, or null if not found.
    /// </summary>
    Task<NodeChatConversationDto?> SetCompactionSummaryAsync(NodeChatSetCompactionSummaryRequest request, CancellationToken cancellationToken = default);

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
