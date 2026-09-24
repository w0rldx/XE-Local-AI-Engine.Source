namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class CreateNodeChatConversationEndpoint : Endpoint<CreateNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly TimeProvider _timeProvider;

    public CreateNodeChatConversationEndpoint(INodeChatPersistenceService chatPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.Conversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CreateNodeChatConversationRequest req, CancellationToken ct)
    {
        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var conversation = await _chatPersistence.CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = req.Title,
                UserId = req.UserId,
                CreatedAtUtc = createdAtUtc,
                AgentDefinitionId = req.AgentDefinitionId
            },
            ct);

        await Send.CreatedAtAsync<GetNodeChatConversationEndpoint>(new
            {
                conversationId = conversation.ConversationId
            },
            conversation.ToResponse(),
            cancellation: ct);
    }
}
