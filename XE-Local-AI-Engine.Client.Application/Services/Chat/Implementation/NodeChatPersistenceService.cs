namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
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

    // Run first by every operator mutation, so no caller skips it; the turn pipeline's writes are guarded where a turn starts.
    private readonly NodeChatMutationGuard _mutationGuard;
    private readonly NodeChatReadModel _readModel;
    private readonly NodeChatVariantBranchService _variants;
    private readonly INodeChatConversationChangeNotifier? _changeNotifier;

    // The usage-provider resolver attributes a thin run envelope from the row's model. The rest are optional: production injects
    // the blob stores, the deletion observers (bound work winds down first) and the change notifier; a test construction skips them.
    public NodeChatPersistenceService(NodeChatPersistenceWriter writer,
        Lazy<IUsageProviderResolver> usageProviderResolver,
        IConversationUploadedFileStore? uploadedFileStore = null,
        IWorkSessionArtifactBlobStore? workSessionArtifactBlobStore = null,
        IEnumerable<IConversationDeletionObserver>? deletionObservers = null,
        INodeChatConversationChangeNotifier? changeNotifier = null)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _changeNotifier = changeNotifier;
        _conversations = new NodeChatConversationCommands(writer, uploadedFileStore, workSessionArtifactBlobStore, deletionObservers);
        _readModel = new NodeChatReadModel(writer);
        _messages = new NodeChatMessageCommands(writer, usageProviderResolver);
        _variants = new NodeChatVariantBranchService(writer, _readModel);
        _feedback = new NodeChatFeedbackStore(writer);
        _mutationGuard = new NodeChatMutationGuard(this);
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

    public Task<NodeChatConversationSearchResultDto> SearchConversationsAsync(NodeChatSearchConversationsRequest request, CancellationToken cancellationToken = default)
    {
        return _readModel.SearchConversationsAsync(request, cancellationToken);
    }

    public Task<NodeChatConversationDto?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _readModel.GetConversationAsync(conversationId, cancellationToken);
    }

    public Task<NodeChatConversationDto?> GetConversationForTurnAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _readModel.GetConversationForTurnAsync(conversationId, cancellationToken);
    }

    public Task<NodeChatConversationPlainMode?> GetConversationPlainModeAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.GetConversationPlainModeAsync(conversationId, cancellationToken);
    }

    public Task<string?> GetConversationOriginAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.GetConversationOriginAsync(conversationId, cancellationToken);
    }

    public Task<IReadOnlyDictionary<Guid, Guid>?> GetSelectedPathAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.GetSelectedPathAsync(conversationId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> SetSelectedPathAsync(NodeChatSetSelectedPathRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _conversations.SetSelectedPathAsync(request, cancellationToken);
    }

    public Task<string?> GetConversationKindAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.GetConversationKindAsync(conversationId, cancellationToken);
    }

    public Task<bool> IsConversationPlainAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return _conversations.IsConversationPlainAsync(conversationId, cancellationToken);
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

    // A terminal write ends a turn, so other clients' lists re-sort and re-preview once per turn, never per streamed token.
    public async Task<NodeChatPersistedMessageDto> TerminalizeAssistantMessageAsync(NodeChatTerminalizeMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var message = await _messages.TerminalizeAssistantMessageAsync(request, cancellationToken);
        await PublishTurnEndedAsync(request.Correlation.ConversationId);
        return message;
    }

    public async Task<NodeChatCancelResultDto> CancelMessageAsync(NodeChatCancelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.Correlation.ConversationId, cancellationToken);
        var result = await _messages.CancelMessageAsync(request, cancellationToken);
        await PublishTurnEndedAsync(request.Correlation.ConversationId);
        return result;
    }

    // Not the caller's token: the row is committed, and a turn cancelled by its token still changed the list. Only a chat
    // is listed, so a work-session, integration or graph turn pushes nothing that every tab would refetch and decrypt.
    private async Task PublishTurnEndedAsync(Guid conversationId)
    {
        if (_changeNotifier is null
            || !string.Equals(await GetConversationKindAsync(conversationId, CancellationToken.None), NodeConversationKind.Chat, StringComparison.Ordinal))
        {
            return;
        }

        await _changeNotifier.PublishAsync(conversationId, CancellationToken.None);
    }

    public async Task<NodeChatDeleteResultDto> DeleteConversationAsync(NodeChatDeleteConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A scheduled answer is read-only but removable: the run viewer's delete and the handler's cleanup come here.
        await _mutationGuard.EnsureDeletableAsync(request.ConversationId, cancellationToken);
        return await _conversations.DeleteConversationAsync(request, cancellationToken);
    }

    public async Task<NodeChatConversationDto> RenameConversationAsync(NodeChatRenameConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _conversations.RenameConversationAsync(request, cancellationToken);
    }

    public async Task<NodeChatConversationDto> SetConversationPinnedAsync(NodeChatSetConversationPinnedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _conversations.SetConversationPinnedAsync(request, cancellationToken);
    }

    public async Task<NodeChatConversationDto> SetConversationArchivedAsync(NodeChatSetConversationArchivedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _conversations.SetConversationArchivedAsync(request, cancellationToken);
    }

    public async Task<NodeChatConversationDto> SetConversationMemoryExcludedAsync(NodeChatSetConversationMemoryExcludedRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _conversations.SetConversationMemoryExcludedAsync(request, cancellationToken);
    }

    public async Task<NodeChatConversationDto> SetConversationPlainAsync(NodeChatSetConversationPlainRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _conversations.SetConversationPlainAsync(request, cancellationToken);
    }

    public async Task<NodeChatConversationDto> SetConversationSamplingOptionsAsync(NodeChatSetConversationSamplingOptionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _conversations.SetConversationSamplingOptionsAsync(request, cancellationToken);
    }

    public Task<NodeChatGuardedWriteOutcome> SetCompactionSummaryAsync(NodeChatSetCompactionSummaryRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.SetCompactionSummaryAsync(request, cancellationToken);
    }

    public Task<NodeChatGuardedWriteOutcome> SetConversationStateAsync(NodeChatSetConversationStateRequest request, CancellationToken cancellationToken = default)
    {
        return _conversations.SetConversationStateAsync(request, cancellationToken);
    }

    public async Task<NodeChatBranchResultDto> BranchConversationAsync(NodeChatBranchConversationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _variants.BranchConversationAsync(request, cancellationToken);
    }

    public async Task<NodeChatMessageVariantDto> CreateMessageVariantAsync(NodeChatCreateMessageVariantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _variants.CreateMessageVariantAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<NodeChatPersistedMessageDto>> ListMessageVariantsAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return _variants.ListMessageVariantsAsync(conversationId, messageId, cancellationToken);
    }

    public async Task<NodeChatMessageFeedbackDto> SetMessageFeedbackAsync(NodeChatSetMessageFeedbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _mutationGuard.EnsureMutableAsync(request.ConversationId, cancellationToken);
        return await _feedback.SetMessageFeedbackAsync(request, cancellationToken);
    }

    public Task<NodeChatMessageFeedbackDto?> GetMessageFeedbackAsync(Guid conversationId, Guid messageId, CancellationToken cancellationToken = default)
    {
        return _feedback.GetMessageFeedbackAsync(conversationId, messageId, cancellationToken);
    }
}
