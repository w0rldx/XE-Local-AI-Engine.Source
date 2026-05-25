namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class GetNodeChatConversationEndpoint(INodeChatPersistenceService chatPersistence)
    : Endpoint<GetNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.ConversationById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetNodeChatConversationRequest req, CancellationToken ct)
    {
        var conversation = await _chatPersistence.GetConversationAsync(req.ConversationId, ct).ConfigureAwait(false);
        if (conversation is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(conversation.ToResponse(), ct).ConfigureAwait(false);
    }
}
