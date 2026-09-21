namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class RenameNodeChatConversationEndpoint : Endpoint<RenameNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly INodeChatMutationGuard _mutationGuard;
    private readonly TimeProvider _timeProvider;

    public RenameNodeChatConversationEndpoint(
        INodeChatPersistenceService chatPersistence,
        INodeChatMutationGuard mutationGuard,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(mutationGuard);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _mutationGuard = mutationGuard;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Patch(LocalApiRoutes.LocalChat.RenameConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        // 409 = the read-only (Origin=Remote) rejection written by the global ConflictExceptionHandler
        // (conflictType = ReadOnlyConversation); the guard exception is never caught here.
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(RenameNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await _chatPersistence.RenameConversationAsync(new NodeChatRenameConversationRequest { ConversationId = req.ConversationId, Title = req.Title, UpdatedAtUtc = updatedAtUtc }, ct);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(updated.ToResponse(), ct);
    }
}
