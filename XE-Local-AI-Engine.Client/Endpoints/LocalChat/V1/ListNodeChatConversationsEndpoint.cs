namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

public sealed class ListNodeChatConversationsEndpoint : Endpoint<ListNodeChatConversationsRequest, ListNodeChatConversationsResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence;

    private readonly IOptions<SecurityOptions> _securityOptions;

    public ListNodeChatConversationsEndpoint(INodeChatPersistenceService chatPersistence, IOptions<SecurityOptions> securityOptions)
    {
        ArgumentNullException.ThrowIfNull(chatPersistence);
        ArgumentNullException.ThrowIfNull(securityOptions);
        _chatPersistence = chatPersistence;
        _securityOptions = securityOptions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.Conversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListNodeChatConversationsRequest req, CancellationToken ct)
    {
        var summaries = await _chatPersistence.ListConversationsAsync(new NodeChatListConversationsRequest
            {
                IncludeArchived = req.IncludeArchived,
                Limit = req.Limit
            },
            ct);

        await Send.OkAsync(new ListNodeChatConversationsResponse
        {
            Items = summaries.Select(static summary => summary.ToResponse()).ToArray(),
            MaxMessageSizeKb = _securityOptions.Value.MaxMessageSizeKb
        }, ct);
    }
}
