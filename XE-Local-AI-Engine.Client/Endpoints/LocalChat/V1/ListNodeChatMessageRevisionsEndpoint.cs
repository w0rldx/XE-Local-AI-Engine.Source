namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Lists every variant of an assistant turn; the hub's <c>RegenerateMessage</c> creates them.</summary>
public sealed class ListNodeChatMessageRevisionsEndpoint : Endpoint<ListNodeChatMessageRevisionsRequest, NodeChatMessageRevisionsResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;

    public ListNodeChatMessageRevisionsEndpoint(INodeChatPersistenceService chatPersistence)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        _chatPersistence = chatPersistence;
    }

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
            throw new NodeChatMessageNotFoundException(req.MessageId);
        }

        await Send.OkAsync(new NodeChatMessageRevisionsResponse
            {
                MessageId = req.MessageId,
                VariantGroupId = variants[0].VariantGroupId,
                Variants = variants.Select(static variant => variant.ToResponse()).ToArray()
            },
            ct);
    }
}
