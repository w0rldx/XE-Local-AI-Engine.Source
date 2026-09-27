namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class CancelNodeChatMessageEndpoint : Endpoint<CancelNodeChatMessageRequest, NodeChatCancelMessageResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly INodeChatStreamCancellationRegistry _streamCancellationRegistry;
    private readonly TimeProvider _timeProvider;

    public CancelNodeChatMessageEndpoint(INodeChatPersistenceService chatPersistence,
        INodeChatStreamCancellationRegistry streamCancellationRegistry,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(streamCancellationRegistry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _streamCancellationRegistry = streamCancellationRegistry;
        _timeProvider = timeProvider;
    }

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
        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = req.ConversationId,
            MessageId = req.MessageId,
            RequestId = req.RequestId
        };

        // CancelMessageAsync throws NodeChatReadOnlyConversationException (an InvalidOperationException) for a Remote conversation, so the catch stays typed to let the
        // 409 through. TryCancel runs first harmlessly: a Remote conversation never registers a stream, since its send is refused.
        try
        {
            _ = _streamCancellationRegistry.TryCancel(correlation);
            var result = await _chatPersistence.CancelMessageAsync(new NodeChatCancelRequest
                {
                    Correlation = correlation,
                    CancelledAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
                },
                ct);

            await Send.OkAsync(new NodeChatCancelMessageResponse
            {
                ConversationId = result.Correlation.ConversationId,
                MessageId = result.Correlation.MessageId,
                RequestId = result.Correlation.RequestId,
                Status = result.Status,
                Cancelled = result.Cancelled
            }, ct);
        }
        catch (NodeChatMessageCorrelationNotFoundException)
        {
            // The TYPED correlation failure only: catching its base InvalidOperationException here would report any unrelated fault raised under CancelMessageAsync as
            // "not found", hiding it from the 500 that says something is actually broken.
            await Send.NotFoundAsync(ct);
        }
    }
}
