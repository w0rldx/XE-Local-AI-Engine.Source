namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class CreateNodeChatConversationEndpoint(
    INodeChatPersistenceService chatPersistence,
    TimeProvider timeProvider) : Endpoint<CreateNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.Conversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateNodeChatConversationRequest req, CancellationToken ct)
    {
        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var conversation = await _chatPersistence.CreateConversationAsync(new NodeChatCreateConversationRequest(req.Title, req.UserId, createdAtUtc),
            ct).ConfigureAwait(false);

        await Send.CreatedAtAsync<GetNodeChatConversationEndpoint>(new
            {
                conversationId = conversation.ConversationId
            },
            conversation.ToResponse(),
            cancellation: ct).ConfigureAwait(false);
    }
}
