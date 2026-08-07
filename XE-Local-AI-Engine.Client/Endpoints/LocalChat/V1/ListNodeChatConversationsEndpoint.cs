namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class ListNodeChatConversationsEndpoint(INodeChatPersistenceService chatPersistence, IOptions<SecurityOptions> securityOptions)
    : Endpoint<ListNodeChatConversationsRequest, ListNodeChatConversationsResponse>
{
    private readonly INodeChatPersistenceService _chatPersistence = chatPersistence ?? throw new ArgumentNullException(nameof(chatPersistence));

    private readonly IOptions<SecurityOptions> _securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalChat.Conversations);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListNodeChatConversationsRequest req, CancellationToken ct)
    {
        var summaries = await _chatPersistence.ListConversationsAsync(new NodeChatListConversationsRequest(req.IncludeArchived, req.Limit),
            ct).ConfigureAwait(false);

        await Send.OkAsync(new ListNodeChatConversationsResponse
        {
            Items = summaries.Select(static summary => summary.ToResponse()).ToArray(),
            MaxMessageSizeKb = _securityOptions.Value.MaxMessageSizeKb
        }, ct).ConfigureAwait(false);
    }
}
