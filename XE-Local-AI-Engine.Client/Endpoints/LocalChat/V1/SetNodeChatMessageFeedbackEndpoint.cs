namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Feedback endpoint: node-local thumbs/comment storage. PUT upserts, GET reads. Guarded — feedback on a
///     remote-mirror message is rejected (consistent with the view-only posture for Origin=Remote).
/// </summary>
public sealed class SetNodeChatMessageFeedbackEndpoint : Endpoint<SetNodeChatMessageFeedbackRequest, NodeChatMessageFeedbackResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly TimeProvider _timeProvider;

    public SetNodeChatMessageFeedbackEndpoint(INodeChatPersistenceService chatPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.LocalChat.MessageFeedback);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(SetNodeChatMessageFeedbackRequest req, CancellationToken ct)
    {
        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var feedback = await _chatPersistence.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest
            {
                ConversationId = req.ConversationId,
                MessageId = req.MessageId,
                Rating = req.Rating,
                Comment = req.Comment,
                UpdatedAtUtc = updatedAtUtc
            },
            ct);

        await Send.OkAsync(feedback.ToResponse(), ct);
    }
}
