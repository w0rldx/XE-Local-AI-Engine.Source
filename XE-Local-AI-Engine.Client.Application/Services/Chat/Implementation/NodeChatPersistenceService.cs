namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Facade over the node chat persistence path, delegating to focused collaborators for conversations, reads,
///     messages, variants and feedback.
/// </summary>
/// <remarks>
///     All of them are composed from the single <see cref="NodeChatPersistenceWriter" />, so the per-conversation and
///     per-message write-key serialization is unchanged. Message content and metadata are AES-encrypted at rest on
///     both the raw-ADO and EF paths through <c>NodeChatContentProtection</c>'s versioned read-both envelope; the
///     collaborators exchange plaintext in memory.
/// </remarks>
public sealed class NodeChatPersistenceService : INodeChatPersistenceService
{
    private readonly NodeChatConversationCommands _conversations;
    private readonly NodeChatFeedbackStore _feedback;
    private readonly NodeChatMessageCommands _messages;
    private readonly NodeChatReadModel _readModel;
    private readonly NodeChatVariantBranchService _variants;

    // All optional: production injects the blob stores (a delete tears down on-disk bytes) and the deletion observers (a
    // feature winds down work bound to the conversation first); a test construction without them skips that work.
    public NodeChatPersistenceService(NodeChatPersistenceWriter writer,
        IConversationUploadedFileStore? uploadedFileStore = null,
        IWorkSessionArtifactBlobStore? workSessionArtifactBlobStore = null,
        IEnumerable<IConversationDeletionObserver>? deletionObservers = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _conversations = new NodeChatConversationCommands(writer, uploadedFileStore, workSessionArtifactBlobStore, deletionObservers);
        _readModel = new NodeChatReadModel(writer);
        _messages = new NodeChatMessageCommands(writer);
        _variants = new NodeChatVariantBranchService(writer, _readModel);
        _feedback = new NodeChatFeedbackStore(writer);
    }

    public Task<NodeChatConversationDto> CreateConversationAsync(NodeChatCreateConversationRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.CreateConversationAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto> EnsureConversationAsync(NodeChatEnsureConversationRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.EnsureConversationAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<NodeChatConversationSummaryDto>> ListConversationsAsync(NodeChatListConversationsRequest request, CancellationToken cancellationToken = default)
    {
        return _readModel.ListConversationsAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _readModel.GetConversationAsync(conversationId, cancellationToken);
    }

    public Task<NodeChatConversationDto?> GetConversationForTurnAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _readModel.GetConversationForTurnAsync(conversationId, cancellationToken);
    }

    public Task<string?> GetConversationOriginAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.GetConversationOriginAsync(conversationId, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, Guid>?> GetSelectedPathAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.GetSelectedPathAsync(conversationId, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, Guid>> SetSelectedPathAsync(NodeChatSetSelectedPathRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.SetSelectedPathAsync(request, cancellationToken);
    }

    public Task<string?> GetConversationKindAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.GetConversationKindAsync(conversationId, cancellationToken);
    }

    public Task<NodeChatInsertMessageIfAbsentResult> InsertMessageIfAbsentAsync(NodeChatInsertMessageIfAbsentRequest request, CancellationToken cancellationToken = default)
    {
        return _messages.InsertMessageIfAbsentAsync(request, cancellationToken);
    }

    public Task DeleteMessageAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return _messages.DeleteMessageAsync(conversationId, messageId, cancellationToken);
    }

    public Task<Guid?> GetMessageConversationIdAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        return _messages.GetMessageConversationIdAsync(messageId, cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> PersistUserMessageAsync(NodeChatPersistUserMessageRequest request, CancellationToken cancellationToken = default)
    {
        return _messages.PersistUserMessageAsync(request, cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> CreateAssistantPlaceholderAsync(NodeChatCreateAssistantPlaceholderRequest request, CancellationToken cancellationToken = default)
    {
        return _messages.CreateAssistantPlaceholderAsync(request, cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> MarkAssistantQueuedAsync(NodeChatMessageCorrelation correlation, long updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return _messages.MarkAssistantQueuedAsync(correlation, updatedAtUtc, cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> MarkAssistantStreamingAsync(NodeChatMessageCorrelation correlation, long updatedAtUtc, CancellationToken cancellationToken = default)
    {
        return _messages.MarkAssistantStreamingAsync(correlation, updatedAtUtc, cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> FlushAssistantPartialAsync(NodeChatPartialFlushRequest request, CancellationToken cancellationToken = default)
    {
        return _messages.FlushAssistantPartialAsync(request, cancellationToken);
    }

    public Task<NodeChatPersistedMessageDto> TerminalizeAssistantMessageAsync(NodeChatTerminalizeMessageRequest request, CancellationToken cancellationToken = default)
    {
        return _messages.TerminalizeAssistantMessageAsync(request, cancellationToken);
    }

    public Task<NodeChatCancelResultDto> CancelMessageAsync(NodeChatCancelRequest request, CancellationToken cancellationToken = default)
    {
        return _messages.CancelMessageAsync(request, cancellationToken);
    }

    public Task<NodeChatDeleteResultDto> DeleteConversationAsync(NodeChatDeleteConversationRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.DeleteConversationAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> RenameConversationAsync(NodeChatRenameConversationRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.RenameConversationAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> SetConversationPinnedAsync(NodeChatSetConversationPinnedRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.SetConversationPinnedAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> SetConversationArchivedAsync(NodeChatSetConversationArchivedRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.SetConversationArchivedAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> SetConversationMemoryExcludedAsync(NodeChatSetConversationMemoryExcludedRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.SetConversationMemoryExcludedAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> SetCompactionSummaryAsync(NodeChatSetCompactionSummaryRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.SetCompactionSummaryAsync(request, cancellationToken);
    }

    public Task<NodeChatBranchResultDto?> BranchConversationAsync(NodeChatBranchConversationRequest request, CancellationToken cancellationToken = default)
    {
        return _variants.BranchConversationAsync(request, cancellationToken);
    }

    public Task<NodeChatMessageVariantDto?> CreateMessageVariantAsync(NodeChatCreateMessageVariantRequest request, CancellationToken cancellationToken = default)
    {
        return _variants.CreateMessageVariantAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<NodeChatPersistedMessageDto>> ListMessageVariantsAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return _variants.ListMessageVariantsAsync(conversationId, messageId, cancellationToken);
    }

    public Task<NodeChatMessageFeedbackDto> SetMessageFeedbackAsync(NodeChatSetMessageFeedbackRequest request, CancellationToken cancellationToken = default)
    {
        return _feedback.SetMessageFeedbackAsync(request, cancellationToken);
    }

    public Task<NodeChatMessageFeedbackDto?> GetMessageFeedbackAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return _feedback.GetMessageFeedbackAsync(conversationId, messageId, cancellationToken);
    }
}
