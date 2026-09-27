namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class ArchiveNodeChatConversationEndpoint : Endpoint<ArchiveNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly TimeProvider _timeProvider;

    public ArchiveNodeChatConversationEndpoint(INodeChatPersistenceService chatPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Patch(LocalApiRoutes.LocalChat.ArchiveConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(ArchiveNodeChatConversationRequest req, CancellationToken ct)
    {
        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await _chatPersistence.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest
        {
            ConversationId = req.ConversationId,
            Archived = req.Archived,
            UpdatedAtUtc = updatedAtUtc
        }, ct);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(updated.ToResponse(), ct);
    }
}
