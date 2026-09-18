namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class GetNodeChatConversationEndpoint : Endpoint<GetNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;

    public GetNodeChatConversationEndpoint(INodeChatPersistenceService chatPersistence)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        _chatPersistence = chatPersistence;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.ConversationById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetNodeChatConversationRequest req, CancellationToken ct)
    {
        var conversation = await _chatPersistence.GetConversationAsync(req.ConversationId, ct);
        if (conversation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(conversation.ToResponse(), ct);
    }
}
