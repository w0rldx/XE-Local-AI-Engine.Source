namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

[Authorize(AuthenticationSchemes = LocalOperatorAuthorization.AuthenticationType, Policy = LocalOperatorAuthorization.OperatorPolicy)]
public sealed class LocalChatHub(INodeChatStreamService streamService) : Hub
{
    public IAsyncEnumerable<ChatStreamEvent> SendMessage(NodeChatStreamRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return streamService.SendMessageAsync(request, cancellationToken);
    }
}
