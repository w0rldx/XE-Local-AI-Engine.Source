namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Stores (or clears) a conversation's own sampling settings, so they follow the chat to every client.
/// </summary>
/// <remarks>
///     The node never applies them: the chat client reads them and sends them with each turn. Same auth path and
///     read-only mutation guard as <see cref="SetNodeChatConversationPlainEndpoint" />.
/// </remarks>
public sealed class SetNodeChatConversationSamplingOptionsEndpoint : Endpoint<SetNodeChatConversationSamplingOptionsRequest, NodeChatConversationResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;
    private readonly TimeProvider _timeProvider;

    public SetNodeChatConversationSamplingOptionsEndpoint(INodeChatPersistenceService chatPersistence,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _chatPersistence = chatPersistence;
        _timeProvider = timeProvider;
    }

    public override void Configure()
    {
        Patch(LocalApiRoutes.LocalChat.SamplingOptionsConversation);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static x => x.ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(SetNodeChatConversationSamplingOptionsRequest req, CancellationToken ct)
    {
        NodeChatConversationDto updated;
        try
        {
            updated = await _chatPersistence.SetConversationSamplingOptionsAsync(new NodeChatSetConversationSamplingOptionsRequest
            {
                ConversationId = req.ConversationId,
                SamplingOptions = req.SamplingOptions?.ToSamplingOptions(),
                UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            }, ct);
        }
        catch (NodeChatInvalidRequestException exception)
        {
            // A work session or other owned conversation keeps no sampling settings: the request's fault, in the validation shape.
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await Send.OkAsync(updated.ToResponse(), ct);
    }
}
