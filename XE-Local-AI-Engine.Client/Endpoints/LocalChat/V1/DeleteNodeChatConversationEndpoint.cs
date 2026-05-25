namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class DeleteNodeChatConversationEndpoint(
    INodeChatPersistenceService chatPersistence,
    TimeProvider timeProvider) : Endpoint<DeleteNodeChatConversationRequest, NodeChatDeleteConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalChat.ConversationById);
        Policies(LocalOperatorAuthorization.OperatorPolicy);
    }

    public override async Task HandleAsync(DeleteNodeChatConversationRequest req, CancellationToken ct)
    {
        var existing = await _chatPersistence.GetConversationAsync(req.ConversationId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        var deletedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var result = await _chatPersistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest(req.ConversationId, deletedAtUtc, req.PurgeImmediately),
            ct).ConfigureAwait(false);

        await Send.OkAsync(new NodeChatDeleteConversationResponse
        {
            ConversationId = result.ConversationId,
            CancelRequested = result.CancelRequested,
            Purged = result.Purged
        }, ct).ConfigureAwait(false);
    }
}
