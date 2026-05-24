namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class ListNodeChatConversationsEndpoint(INodeChatPersistenceService chatPersistence)
    : Endpoint<ListNodeChatConversationsRequest, ListNodeChatConversationsResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.Conversations);
        Policies(LocalOperatorAuthorization.OperatorPolicy);
    }

    public override async Task HandleAsync(ListNodeChatConversationsRequest req, CancellationToken ct)
    {
        var summaries = await _chatPersistence.ListConversationsAsync(
            new NodeChatListConversationsRequest(req.IncludeArchived, req.Limit),
            ct).ConfigureAwait(false);

        await Send.OkAsync(new ListNodeChatConversationsResponse
        {
            Items = summaries.Select(static summary => summary.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}
