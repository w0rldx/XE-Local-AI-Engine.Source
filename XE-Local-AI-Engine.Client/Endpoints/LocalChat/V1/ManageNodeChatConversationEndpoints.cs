namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class RenameNodeChatConversationEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<RenameNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Patch(LocalApiRoutes.LocalChat.RenameConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        // 409 = the read-only (Origin=Remote) rejection written by the global ConflictExceptionHandler
        // (conflictType = ReadOnlyConversation); the guard exception is never caught here.
        Description(static x => x.ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RenameNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await _chatPersistence.RenameConversationAsync(new NodeChatRenameConversationRequest(req.ConversationId, req.Title, updatedAtUtc), ct).ConfigureAwait(false);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class PinNodeChatConversationEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<PinNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Patch(LocalApiRoutes.LocalChat.PinConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(PinNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await _chatPersistence.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest(req.ConversationId, req.IsPinned, updatedAtUtc), ct).ConfigureAwait(false);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class ArchiveNodeChatConversationEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<ArchiveNodeChatConversationRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Patch(LocalApiRoutes.LocalChat.ArchiveConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ArchiveNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await _chatPersistence.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest(req.ConversationId, req.Archived, updatedAtUtc), ct).ConfigureAwait(false);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
    }
}

/// <summary>
///     Sets the per-conversation temporary-chat (<c>memory_excluded</c>) override (adaptive memory). Stays on the chat
///     auth path (Operator policy, same as the other conversation mutations) — NOT the agent-management surface — and
///     honors the read-only mutation guard like rename/pin/archive.
/// </summary>
public sealed class SetNodeChatConversationMemoryExcludedEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    TimeProvider timeProvider) : Endpoint<SetNodeChatConversationMemoryExcludedRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Patch(LocalApiRoutes.LocalChat.MemoryExcludedConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(SetNodeChatConversationMemoryExcludedRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var updated = await _chatPersistence.SetConversationMemoryExcludedAsync(new NodeChatSetConversationMemoryExcludedRequest(req.ConversationId, req.MemoryExcluded, updatedAtUtc), ct)
                                            .ConfigureAwait(false);

        if (updated is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
    }
}
