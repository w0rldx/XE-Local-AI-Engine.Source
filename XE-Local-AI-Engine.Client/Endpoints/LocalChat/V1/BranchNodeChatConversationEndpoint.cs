namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Branch endpoint: POST clones the conversation up to the target message into a new local-origin conversation.
/// </summary>
/// <remarks>
///     Guarded — branching FROM a remote mirror is rejected with 409: the source is read-only, and the branch would
///     carry remote content the node can no longer re-drive.
/// </remarks>
public sealed class BranchNodeChatConversationEndpoint : Endpoint<BranchNodeChatConversationRequest, NodeChatBranchConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly INodeChatMutationGuard _mutationGuard;
    private readonly TimeProvider _timeProvider;

    public BranchNodeChatConversationEndpoint(INodeChatPersistenceService chatPersistence,
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
        Post(LocalApiRoutes.LocalChat.BranchConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        // 409 = the read-only (Origin=Remote) rejection written by the global ConflictExceptionHandler
        // (conflictType = ReadOnlyConversation); the guard exception is never caught here.
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(BranchNodeChatConversationRequest req, CancellationToken ct)
    {
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct);

        var createdAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        // A selected-revision entry that fails integrity validation (not a conversation member, or the wrong group) throws NodeChatInvalidBranchSelectionException, which
        // the global DomainValidationExceptionHandler answers with a 400: fail closed rather than branching a path the caller did not actually specify.
        var branched = await _chatPersistence.BranchConversationAsync(new NodeChatBranchConversationRequest
            {
                ConversationId = req.ConversationId,
                MessageId = req.MessageId,
                CreatedAtUtc = createdAtUtc,
                SelectedRevisions = req.SelectedRevisions
            },
            ct);

        if (branched is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(branched.ToResponse(), ct);
    }
}
