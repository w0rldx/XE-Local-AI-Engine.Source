namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Facade over the node chat persistence path. Implements <see cref="INodeChatPersistenceService" /> by delegating
///     to focused collaborators (conversation commands, read model, message commands, variant/branch, feedback), all
///     composed from the single <see cref="NodeChatPersistenceWriter" /> so the per-conversation/per-message write-key
///     serialization is unchanged. Message content and metadata are AES-encrypted at rest on both the raw-ADO and EF
///     paths (versioned read-both envelope via <c>NodeChatContentProtection</c>); the collaborators exchange plaintext
///     in memory.
/// </summary>
public sealed class NodeChatPersistenceService : INodeChatPersistenceService
{
    private readonly NodeChatConversationCommands _conversations;
    private readonly NodeChatFeedbackStore _feedback;
    private readonly NodeChatMessageCommands _messages;
    private readonly NodeChatReadModel _readModel;
    private readonly NodeChatVariantBranchService _variants;

    // The uploaded-file store is an optional dependency: the DI container injects the real singleton in production so
    // conversation-delete also tears down on-disk attachments, while existing single-arg test constructions stay valid
    // (they exercise paths that create no uploaded files, so a null store simply skips the disk cleanup).
    public NodeChatPersistenceService(NodeChatPersistenceWriter writer, IConversationUploadedFileStore? uploadedFileStore = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _conversations = new NodeChatConversationCommands(writer, uploadedFileStore);
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
