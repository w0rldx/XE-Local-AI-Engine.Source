namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Branch endpoint: POST clones the conversation up to the target message into a new Origin=Local
///     conversation. Guarded — branching FROM a remote mirror is rejected with 409 (the source is read-only;
///     the branch would carry remote content the node can no longer re-drive).
/// </summary>
public sealed class BranchNodeChatConversationEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<BranchNodeChatConversationRequest, NodeChatBranchConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.BranchConversation);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(BranchNodeChatConversationRequest req, CancellationToken ct)
    {
        try
        {
            await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);
        }
        catch (NodeChatReadOnlyConversationException)
        {
            await Send.ResultAsync(Results.Conflict(NodeChatConflictResponse.ReadOnly)).ConfigureAwait(false);
            return;
        }

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var branched = await _chatPersistence.BranchConversationAsync(new NodeChatBranchConversationRequest(req.ConversationId, req.MessageId, createdAtUtc), ct).ConfigureAwait(false);

        if (branched is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(branched.ToResponse(), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Revision endpoint: POST records a regenerated assistant turn as a SIBLING VARIANT (never in-place)
///     and returns the freshly minted placeholder; GET lists every variant of the turn. Both guarded.
/// </summary>
public sealed class CreateNodeChatMessageRevisionEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<ListNodeChatMessageRevisionsRequest, NodeChatMessageRevisionsResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.MessageRevisions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListNodeChatMessageRevisionsRequest req, CancellationToken ct)
    {
        try
        {
            await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);
        }
        catch (NodeChatReadOnlyConversationException)
        {
            await Send.ResultAsync(Results.Conflict(NodeChatConflictResponse.ReadOnly)).ConfigureAwait(false);
            return;
        }

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var variant = await _chatPersistence.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(req.ConversationId,
                req.MessageId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                createdAtUtc),
            ct).ConfigureAwait(false);

        if (variant is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        var variants = await _chatPersistence.ListMessageVariantsAsync(req.ConversationId, variant.Variant.MessageId, ct).ConfigureAwait(false);
        await Send.OkAsync(BuildResponse(variant.OriginalMessageId, variant.VariantGroupId, variants), ct).ConfigureAwait(false);
    }

    internal static NodeChatMessageRevisionsResponse BuildResponse(Guid messageId, Guid? variantGroupId, IReadOnlyList<NodeChatPersistedMessageDto> variants)
    {
        return new NodeChatMessageRevisionsResponse
        {
            MessageId = messageId,
            VariantGroupId = variantGroupId,
            Variants = variants.Select(static variant => variant.ToResponse()).ToArray()
        };
    }
}

public sealed class ListNodeChatMessageRevisionsEndpoint(INodeChatPersistenceService chatPersistence) : Endpoint<ListNodeChatMessageRevisionsRequest, NodeChatMessageRevisionsResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.MessageRevisions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListNodeChatMessageRevisionsRequest req, CancellationToken ct)
    {
        var variants = await _chatPersistence.ListMessageVariantsAsync(req.ConversationId, req.MessageId, ct).ConfigureAwait(false);
        if (variants.Count == 0)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        var variantGroupId = variants[0].VariantGroupId;
        await Send.OkAsync(CreateNodeChatMessageRevisionEndpoint.BuildResponse(req.MessageId, variantGroupId, variants), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Feedback endpoint: node-local thumbs/comment storage. PUT upserts, GET reads. Guarded — feedback on a
///     remote-mirror message is rejected (consistent with the view-only posture for Origin=Remote).
/// </summary>
public sealed class SetNodeChatMessageFeedbackEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<SetNodeChatMessageFeedbackRequest, NodeChatMessageFeedbackResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Put(LocalApiRoutes.LocalChat.MessageFeedback);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetNodeChatMessageFeedbackRequest req, CancellationToken ct)
    {
        try
        {
            await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);
        }
        catch (NodeChatReadOnlyConversationException)
        {
            await Send.ResultAsync(Results.Conflict(NodeChatConflictResponse.ReadOnly)).ConfigureAwait(false);
            return;
        }

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var feedback = await _chatPersistence.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(req.ConversationId,
                req.MessageId,
                req.Rating,
                req.Comment,
                updatedAtUtc),
            ct).ConfigureAwait(false);

        await Send.OkAsync(feedback.ToResponse(), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Selected path (conversation tree): PUT upserts the conversation's selected-path map
///     {variantGroupId-&gt;selectedMessageId} WITHOUT sending a message, so navigating &lt; N/N &gt; variants survives a
///     reload. An empty/absent map clears the stored selection. Guarded — persisting a selection on a
///     remote-mirror (Origin=Remote) conversation is rejected with 409, consistent with the view-only posture.
/// </summary>
public sealed class SetNodeChatSelectedPathEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<SetNodeChatSelectedPathRequest, NodeChatSelectedPathResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Put(LocalApiRoutes.LocalChat.SelectedPath);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetNodeChatSelectedPathRequest req, CancellationToken ct)
    {
        try
        {
            await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);
        }
        catch (NodeChatReadOnlyConversationException)
        {
            await Send.ResultAsync(Results.Conflict(NodeChatConflictResponse.ReadOnly)).ConfigureAwait(false);
            return;
        }

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var persisted = await _chatPersistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(req.ConversationId,
                req.SelectedPath,
                updatedAtUtc),
            ct).ConfigureAwait(false);

        await Send.OkAsync(new NodeChatSelectedPathResponse
        {
            ConversationId = req.ConversationId,
            SelectedPath = persisted
        }, ct).ConfigureAwait(false);
    }
}

public sealed class GetNodeChatMessageFeedbackEndpoint(INodeChatPersistenceService chatPersistence) : Endpoint<GetNodeChatMessageFeedbackRequest, NodeChatMessageFeedbackResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.MessageFeedback);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetNodeChatMessageFeedbackRequest req, CancellationToken ct)
    {
        var feedback = await _chatPersistence.GetMessageFeedbackAsync(req.ConversationId, req.MessageId, ct).ConfigureAwait(false);
        if (feedback is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(feedback.ToResponse(), ct).ConfigureAwait(false);
    }
}
