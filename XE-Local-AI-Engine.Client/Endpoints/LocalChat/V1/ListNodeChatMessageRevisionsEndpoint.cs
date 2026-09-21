namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

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
            await Send.NotFoundAsync(ct);
            return;
        }

        var variantGroupId = variants[0].VariantGroupId;
        await Send.OkAsync(CreateNodeChatMessageRevisionEndpoint.BuildResponse(req.MessageId, variantGroupId, variants), ct);
    }
}
