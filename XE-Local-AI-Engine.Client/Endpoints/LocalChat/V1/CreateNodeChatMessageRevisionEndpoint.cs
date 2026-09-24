namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Revision endpoint: POST records a regenerated assistant turn as a SIBLING VARIANT (never in-place)
///     and returns the freshly minted placeholder; GET lists every variant of the turn. Both guarded.
/// </summary>
public sealed class CreateNodeChatMessageRevisionEndpoint : Endpoint<ListNodeChatMessageRevisionsRequest, NodeChatMessageRevisionsResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly INodeChatMutationGuard _mutationGuard;
    private readonly TimeProvider _timeProvider;

    public CreateNodeChatMessageRevisionEndpoint(INodeChatPersistenceService chatPersistence,
        INodeChatMutationGuard mutationGuard,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(mutationGuard);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _mutationGuard = mutationGuard;
        _timeProvider = timeProvider;
    }

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
        var variant = await _chatPersistence.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest
            {
                ConversationId = req.ConversationId,
                OriginalMessageId = req.MessageId,
                NewMessageId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                CreatedAtUtc = createdAtUtc
            },
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
