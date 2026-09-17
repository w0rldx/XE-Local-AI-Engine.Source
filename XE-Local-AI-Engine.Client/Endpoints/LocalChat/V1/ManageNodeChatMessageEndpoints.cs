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
        // 409 = the read-only (Origin=Remote) rejection written by the global ConflictExceptionHandler
        // (conflictType = ReadOnlyConversation); the guard exception is never caught here.
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(BranchNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // A selected-revision entry that fails integrity validation (not a conversation member / wrong group) throws
        // NodeChatInvalidBranchSelectionException, which the global DomainValidationExceptionHandler answers with a
        // 400 — fail closed rather than branching a path the caller did not actually specify.
        var branched = await _chatPersistence.BranchConversationAsync(new NodeChatBranchConversationRequest(req.ConversationId, req.MessageId, createdAtUtc, req.SelectedRevisions),
            ct);

        if (branched is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(branched.ToResponse(), ct);
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
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(ListNodeChatMessageRevisionsRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var variant = await _chatPersistence.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(req.ConversationId,
                req.MessageId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                createdAtUtc),
            ct);

        if (variant is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var variants = await _chatPersistence.ListMessageVariantsAsync(req.ConversationId, variant.Variant.MessageId, ct);
        await Send.OkAsync(BuildResponse(variant.OriginalMessageId, variant.VariantGroupId, variants), ct);
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
        var variants = await _chatPersistence.ListMessageVariantsAsync(req.ConversationId, req.MessageId, ct);
        if (variants.Count == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var variantGroupId = variants[0].VariantGroupId;
        await Send.OkAsync(CreateNodeChatMessageRevisionEndpoint.BuildResponse(req.MessageId, variantGroupId, variants), ct);
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
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(SetNodeChatMessageFeedbackRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var feedback = await _chatPersistence.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(req.ConversationId,
                req.MessageId,
                req.Rating,
                req.Comment,
                updatedAtUtc),
            ct);

        await Send.OkAsync(feedback.ToResponse(), ct);
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
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(SetNodeChatSelectedPathRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var persisted = await _chatPersistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(req.ConversationId,
                req.SelectedPath,
                updatedAtUtc),
            ct);

        await Send.OkAsync(new NodeChatSelectedPathResponse
        {
            ConversationId = req.ConversationId,
            SelectedPath = persisted
        }, ct);
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
        var feedback = await _chatPersistence.GetMessageFeedbackAsync(req.ConversationId, req.MessageId, ct);
        if (feedback is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(feedback.ToResponse(), ct);
    }
}
