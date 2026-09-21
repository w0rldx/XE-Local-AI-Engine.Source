namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class GetNodeChatMessageFeedbackEndpoint : Endpoint<GetNodeChatMessageFeedbackRequest, NodeChatMessageFeedbackResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;

    public GetNodeChatMessageFeedbackEndpoint(INodeChatPersistenceService chatPersistence)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        _chatPersistence = chatPersistence;
    }

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
