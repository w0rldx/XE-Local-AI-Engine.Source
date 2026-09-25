namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Read-only view of one conversation's distilled state and compaction synopsis. Operator-gated.
/// </summary>
public sealed class GetNodeChatConversationContextStateEndpoint
    : Endpoint<GetNodeChatConversationContextStateRequest, NodeChatConversationContextStateResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;

    public GetNodeChatConversationContextStateEndpoint(INodeChatPersistenceService chatPersistence)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        _chatPersistence = chatPersistence;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.ConversationContextState);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetNodeChatConversationContextStateRequest req, CancellationToken ct)
    {
        var conversation = await _chatPersistence.GetConversationAsync(req.ConversationId, ct);
        if (conversation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(conversation.ToContextStateResponse(), ct);
    }
}
