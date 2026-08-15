namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class CancelNodeChatMessageEndpoint(
    INodeChatPersistenceService chatPersistence,
    INodeChatMutationGuard mutationGuard,
    INodeChatStreamCancellationRegistry streamCancellationRegistry,
    TimeProvider timeProvider) : Endpoint<CancelNodeChatMessageRequest, NodeChatCancelMessageResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));
    private readonly INodeChatMutationGuard _mutationGuard = mutationGuard ?? throw new ArgumentNullException(nameof(mutationGuard));
    private readonly INodeChatStreamCancellationRegistry _streamCancellationRegistry = streamCancellationRegistry ?? throw new ArgumentNullException(nameof(streamCancellationRegistry));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.Cancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // 409 = the read-only (Origin=Remote) rejection written by the global ConflictExceptionHandler
        // (conflictType = ReadOnlyConversation); the guard exception is never caught here.
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(CancelNodeChatMessageRequest req, CancellationToken ct)
    {
        var correlation = new NodeChatMessageCorrelation(req.ConversationId, req.MessageId, req.RequestId);

        // The guard runs OUTSIDE the try below on purpose: NodeChatReadOnlyConversationException derives from
        // InvalidOperationException, so inside it the generic -> NotFound arm would swallow the 409 the global
        // ConflictExceptionHandler must write.
        await _mutationGuard.EnsureMutableAsync(req.ConversationId, ct).ConfigureAwait(false);

        try
        {
            _ = _streamCancellationRegistry.TryCancel(correlation);
            var result = await _chatPersistence.CancelMessageAsync(new NodeChatCancelRequest(correlation, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
                ct).ConfigureAwait(false);

            await Send.OkAsync(new NodeChatCancelMessageResponse
            {
                ConversationId = result.Correlation.ConversationId,
                MessageId = result.Correlation.MessageId,
                RequestId = result.Correlation.RequestId,
                Status = result.Status,
                Cancelled = result.Cancelled
            }, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}
