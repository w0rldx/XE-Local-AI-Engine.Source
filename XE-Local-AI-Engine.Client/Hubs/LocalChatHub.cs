namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class LocalChatHub(INodeChatStreamService streamService) : Hub
{
    public IAsyncEnumerable<ChatStreamEvent> SendMessage(NodeChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return streamService.SendMessageAsync(request, cancellationToken);
    }
}
