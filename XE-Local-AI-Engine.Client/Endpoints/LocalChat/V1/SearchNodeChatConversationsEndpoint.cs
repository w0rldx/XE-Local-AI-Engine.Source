namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>Searches chat conversations by title and message text, newest first.</summary>
/// <remarks>Operator-gated like the list. Work-session and integration transcripts and deleted conversations never match.</remarks>
public sealed class SearchNodeChatConversationsEndpoint : Endpoint<SearchNodeChatConversationsRequest, SearchNodeChatConversationsResponse>
{
    private const int DefaultLimit = 50;

    private readonly INodeChatPersistenceService _chatPersistence;

    public SearchNodeChatConversationsEndpoint(INodeChatPersistenceService chatPersistence)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        _chatPersistence = chatPersistence;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.SearchConversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SearchNodeChatConversationsRequest req, CancellationToken ct)
    {
        var result = await _chatPersistence.SearchConversationsAsync(new NodeChatSearchConversationsRequest
            {
                Query = req.Query,
                IncludeArchived = req.IncludeArchived,
                Limit = req.Limit ?? DefaultLimit
            },
            ct);

        await Send.OkAsync(result.ToResponse(), ct);
    }
}
