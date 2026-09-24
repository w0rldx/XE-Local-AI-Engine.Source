namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Sets the per-conversation temporary-chat (<c>memory_excluded</c>) override (adaptive memory).
/// </summary>
/// <remarks>
///     Stays on the chat auth path — the Operator policy the other conversation mutations carry, NOT the
///     agent-management surface — and honors the read-only mutation guard like rename, pin and archive.
/// </remarks>
public sealed class SetNodeChatConversationMemoryExcludedEndpoint : Endpoint<SetNodeChatConversationMemoryExcludedRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly INodeChatMutationGuard _mutationGuard;
    private readonly TimeProvider _timeProvider;

    public SetNodeChatConversationMemoryExcludedEndpoint(INodeChatPersistenceService chatPersistence,
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
        Patch(LocalApiRoutes.LocalChat.MemoryExcludedConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(SetNodeChatConversationMemoryExcludedRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await _chatPersistence.SetConversationMemoryExcludedAsync(new NodeChatSetConversationMemoryExcludedRequest
        {
            ConversationId = req.ConversationId,
            MemoryExcluded = req.MemoryExcluded,
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
