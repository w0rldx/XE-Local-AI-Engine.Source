namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class DeleteNodeChatConversationEndpoint : Endpoint<DeleteNodeChatConversationRequest, NodeChatDeleteConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly TimeProvider _timeProvider;

    public DeleteNodeChatConversationEndpoint(
        INodeChatPersistenceService chatPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalChat.ConversationById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteNodeChatConversationRequest req, CancellationToken ct)
    {
        var existing = await _chatPersistence.GetConversationAsync(req.ConversationId, ct);
        if (existing is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var deletedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var result = await _chatPersistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest { ConversationId = req.ConversationId, DeletedAtUtc = deletedAtUtc, PurgeImmediately = req.PurgeImmediately },
            ct);

        await Send.OkAsync(new NodeChatDeleteConversationResponse
        {
            ConversationId = result.ConversationId,
            CancelRequested = result.CancelRequested,
            Purged = result.Purged
        }, ct);
    }
}
