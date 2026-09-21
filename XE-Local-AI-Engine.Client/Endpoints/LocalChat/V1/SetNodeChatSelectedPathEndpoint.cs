namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Selected path (conversation tree): PUT upserts the conversation's selected-path map
///     {variantGroupId-&gt;selectedMessageId} WITHOUT sending a message, so variant navigation survives a reload.
/// </summary>
/// <remarks>
///     An empty or absent map clears the stored selection. Guarded — persisting a selection on a remote-mirror
///     conversation is rejected with 409, consistent with the view-only posture.
/// </remarks>
public sealed class SetNodeChatSelectedPathEndpoint : Endpoint<SetNodeChatSelectedPathRequest, NodeChatSelectedPathResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly INodeChatMutationGuard _mutationGuard;
    private readonly TimeProvider _timeProvider;

    public SetNodeChatSelectedPathEndpoint(
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
        Put(LocalApiRoutes.LocalChat.SelectedPath);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(SetNodeChatSelectedPathRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var persisted = await _chatPersistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest
        {
            ConversationId = req.ConversationId,
            SelectedPath = req.SelectedPath,
            UpdatedAtUtc = updatedAtUtc
        },
            ct);

        await Send.OkAsync(new NodeChatSelectedPathResponse
        {
            ConversationId = req.ConversationId,
            SelectedPath = persisted
        }, ct);
    }
}
